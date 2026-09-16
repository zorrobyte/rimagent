"""Episode runner: keeps the game running, wakes the agent, scores episodes, starts the next game."""
from __future__ import annotations

import subprocess
import threading
import time
from typing import Any

from . import braingit, memory, reflect, scorecard
from .bridge import Bridge, BridgeError
from .bus import BUS, Bus
from .context import Context
from .llm import LLM
from .loop import situation_packet, think
from .paths import ROOT
from .registry import Registry
from .tools import brain as brain_tools
from .tools import knowledge as knowledge_tools
from .tools import meta as meta_tools
from .watchers import run_all as run_watchers

TICKS_PER_HOUR = 2500
TICKS_PER_DAY = 60000


class Controls:
    """What the dashboard can poke."""

    def __init__(self, runner: "Runner"):
        self.r = runner
        self.paused = False

    def pause(self):
        self.paused = True
        self.r.bus.emit("log", {"text": "agent paused by operator"})

    def resume(self):
        self.paused = False
        self.r.bus.emit("log", {"text": "agent resumed by operator"})

    def think_now(self):
        self.r.force_think = "operator"

    def end_episode(self):
        self.r.force_end = "operator ended the episode"

    def set_no_pause(self, value: bool):
        # dashboard toggle: no-pause = never pause even for danger (think at 1x instead)
        self.r.cfg["play"]["danger_think_speed"] = 1 if value else 0

    def kill(self):
        self.r.stop = True

    def say(self, text: str, remember: bool = True):
        """Operator message: shown in the feed, handed to the model (mid-step or next step), and wakes it.
        Also logged to brain/memory/operator.md so the episode reflection can fold missed tips into skills."""
        memory.operator_append(text)
        self.r.operator_inbox.append(text)
        self.r.bus.emit("operator", {"text": text})
        self.r.force_think = "operator message"


class Runner:
    def __init__(self, cfg: dict[str, Any], bus: Bus | None = None):
        self.cfg = cfg
        self.bus = bus or BUS
        self.bridge = Bridge(cfg["bridge"]["url"])
        self.llm = LLM()
        self.registry = Registry()
        for mod in (knowledge_tools, brain_tools, meta_tools):
            self.registry.add_module(mod)
        self.ctx = Context(bridge=self.bridge, llm=self.llm, registry=self.registry, config=cfg, emit=self.bus.emit)
        self.controls = Controls(self)
        self.stop = False
        self.force_think: str | None = None
        self.force_end: str | None = None
        self.operator_inbox: list[str] = []
        self.ctx.extra["operator_inbox"] = self.operator_inbox  # shared with the loop so messages land mid-step
        # episode state
        self.episode = len(scorecard.history(10_000))
        self.seed = ""
        self.events: list[dict[str, Any]] = []
        self.pending_events: list[dict[str, Any]] = []
        self.pending_alerts: list[dict[str, Any]] = []
        self.step_notes: list[str] = []
        self.deaths = 0
        self.raids = 0
        self.next_wake_tick = 0
        self.wake_kinds: set[str] = set(cfg["play"].get("wake_on_kinds", []))
        self.last_day = -1
        self.last_improve_day = 0
        self.start_day = 0
        self.thinking = False
        self._status_at = 0.0
        self._last_alive = time.time()
        self._alerts_at = 0.0
        self._seen_alerts: dict[str, int] = {}   # label -> tick last woken for it
        self._last_step_end_tick = 0
        self.critical_kinds = set(cfg["play"].get("critical_kinds", ["dialog", "danger", "manhunter", "hostile_group", "colonist_downed", "colonist_died", "mental_break", "building_lost"]))

    # ---------- lifecycle ----------
    def run(self) -> None:
        self.bus.emit("log", {"text": "runner starting"})
        self.bridge.wait_alive()
        self.registry.add_bridge_methods(self.bridge.methods())
        self.registry.reload_brain()
        self.bus.emit("log", {"text": f"{len(self.registry.tools)} tools registered ({sum(1 for t in self.registry.tools.values() if t.source == 'bridge')} bridge)"})
        while not self.stop:
            try:
                self.ensure_game()
                self.play_until_episode_end()
            except BridgeError as e:
                self.bus.emit("error", {"text": f"bridge: {e}"})
                self.recover_game()
            except Exception as e:  # noqa: BLE001
                import traceback
                self.bus.emit("error", {"text": f"runner: {e}\n{traceback.format_exc(limit=5)}"})
                time.sleep(5)
        self.bus.emit("log", {"text": "runner stopped"})

    def ensure_game(self) -> None:
        st = self.bridge.status()
        if st.get("state") == "playing":
            if not self.seed:
                # Resumed into an existing game (agent restarted): continue it as the current episode.
                self.seed = st.get("seed") or "resumed"
                self.episode = max(self.episode, 1)
                self.start_day = int(st.get("day", 0))
                self.last_day = self.start_day
                self.ctx.last_seq = int(st.get("seq", 0))
                self.ctx.episode, self.ctx.seed = self.episode, self.seed
                self.bus.emit("episode_start", {"episode": self.episode, "seed": self.seed, "resumed": True, "day": self.start_day})
                self.force_think = "agent (re)started mid-game"
            return
        if st.get("state") == "loading":
            # Startup or a scene change in progress: wait until it settles (menu or playing), then decide.
            t0 = time.time()
            while time.time() - t0 < 600 and self.bridge.status().get("state") == "loading":
                time.sleep(2)
            return self.ensure_game()
        self.new_game()

    def new_game(self) -> None:
        play = self.cfg["play"]
        seeds = play.get("seeds") or ["rimagent-1"]
        self.episode += 1
        self.seed = seeds[(self.episode - 1) % len(seeds)]
        self.bus.emit("status", {"phase": "loading", "episode": self.episode, "seed": self.seed})
        self.bridge.call("game.new_game", seed=self.seed, scenario=play.get("scenario", "Crashlanded"), storyteller=play.get("storyteller", "Cassandra"), difficulty=play.get("difficulty", "Rough"))
        time.sleep(3)
        st = self.bridge.wait_for("playing", 600)
        self.events, self.pending_events, self.pending_alerts, self.step_notes = [], [], [], []
        self.deaths = self.raids = 0
        self.start_day = self.last_day = int(st.get("day", 0))
        self.last_improve_day = self.start_day
        self.ctx.last_seq = 0
        self.ctx.episode, self.ctx.seed = self.episode, self.seed
        memory.notebook_reset(f"# Colony notebook — episode {self.episode}, seed {self.seed}\n\n(new game; nothing decided yet)")
        self.bus.emit("episode_start", {"episode": self.episode, "seed": self.seed})
        self.force_think = "new game started"

    def recover_game(self) -> None:
        """Bridge went away: wait, or relaunch the game if configured."""
        self.bus.emit("status", {"phase": "loading"})
        for _ in range(20):
            if self.stop:
                return
            if self.bridge.health():
                return
            time.sleep(3)
        if self.cfg["play"].get("auto_restart_game", True):
            self.bus.emit("log", {"text": "game unreachable for 60s; relaunching via script/restart-game.sh"})
            subprocess.run([str(ROOT / "script" / "restart-game.sh")], timeout=60)
            self.bridge.wait_alive(600)
            self.registry.add_bridge_methods(self.bridge.methods())
            # prefer the autosave of the current episode
            try:
                saves = [s["name"] for s in self.bridge.call("game.list_saves")]
                if "rimagent-autosave" in saves and self.seed:
                    self.bridge.call("game.load", name="rimagent-autosave")
                    self.bridge.wait_for("playing", 600)
            except BridgeError as e:
                self.bus.emit("error", {"text": f"autosave load failed: {e}"})

    # ---------- main loop ----------
    def play_until_episode_end(self) -> None:
        play = self.cfg["play"]
        self.bridge.call("game.speed", speed=int(play.get("speed", 3)))
        self.next_wake_tick = 0
        while not self.stop:
            st = self.bridge.status()
            self._last_alive = time.time()
            if st.get("state") != "playing":
                if st.get("state") == "loading":
                    time.sleep(2)
                    continue
                self.bus.emit("log", {"text": f"game state is {st.get('state')}; ending episode"})
                self.end_episode("game left the play state")
                return
            tick = int(st.get("tick", 0))
            day = int(st.get("day", 0))
            self.emit_status(st)
            new_events = self.poll_events()
            alerts = run_watchers(self.ctx, new_events)
            self.pending_alerts += alerts
            # episode end conditions
            if int(st.get("colonists", 0)) == 0 and day > self.start_day:
                self.end_episode("all colonists dead")
                return
            if self.force_end:
                reason, self.force_end = self.force_end, None
                self.end_episode(reason)
                return
            if day - self.start_day >= int(play.get("max_days", 60)):
                self.end_episode(f"reached max_days ({play.get('max_days')})")
                return
            # day rollover: autosave + maybe improvement pass
            if day != self.last_day:
                self.last_day = day
                if play.get("autosave", True):
                    try:
                        self.bridge.call("game.save", name="rimagent-autosave")
                    except BridgeError as e:
                        self.bus.emit("error", {"text": f"autosave: {e}"})
                first = int(play.get("first_improve_day", 1))
                due = (day - self.start_day >= first and self.last_improve_day == self.start_day) or (day - self.last_improve_day >= int(play.get("improve_every_days", 3)))
                if due:
                    self.last_improve_day = day
                    self.with_pause(lambda: self.run_improve(day))
            if self.controls.paused:
                time.sleep(1)
                continue
            trigger = self.wake_trigger(tick, new_events)
            if trigger:
                self.with_pause(lambda: self.play_step(trigger, tick), urgent=self.is_urgent(trigger))
                if self.ctx.end_episode_reason:
                    self.end_episode(self.ctx.end_episode_reason)
                    return
                continue
            time.sleep(0.5)

    def emit_status(self, st: dict[str, Any]) -> None:
        if time.time() - self._status_at < 1.0:
            return
        self._status_at = time.time()
        self.bus.emit("status", {**st, "episode": self.episode, "seed": self.seed, "phase": "thinking" if self.thinking else "playing", "deaths": self.deaths, "raids": self.raids, "next_wake_tick": self.next_wake_tick})

    def poll_events(self) -> list[dict[str, Any]]:
        data = self.bridge.events(self.ctx.last_seq, 500)
        evs = data.get("events", [])
        if not evs:
            return []
        self.ctx.last_seq = int(data.get("last_seq", self.ctx.last_seq))
        for e in evs:
            self.bus.emit("ledger", e)
            k = e.get("kind")
            if k == "colonist_died":
                self.deaths += 1
            elif k == "hostile_group":
                self.raids += 1
        self.events += evs
        self.pending_events += evs
        self.ctx.recent_events = self.pending_events[-100:]
        return evs

    def wake_trigger(self, tick: int, new_events: list[dict[str, Any]]) -> str | None:
        if self.force_think:
            t, self.force_think = self.force_think, None
            return t
        for a in self.pending_alerts:
            if a.get("wake"):
                return f"watcher alert: {a.get('text')}"
        play = self.cfg["play"]
        cooldown = int(float(play.get("event_cooldown_hours", 1)) * TICKS_PER_HOUR)
        recently = tick - self._last_step_end_tick < cooldown
        kinds = self.wake_kinds | set(self.ctx.wake.on_kinds or [])
        for e in new_events:
            k = e.get("kind")
            if k in self.critical_kinds or (k in kinds and not recently):
                return f"event: {k} — {e.get('text', '')}"
        alert = self.game_alert_trigger(tick)
        if alert:
            return alert
        if tick >= self.next_wake_tick:
            return "scheduled check-in"
        return None

    def game_alert_trigger(self, tick: int) -> str | None:
        """Wake on new High/Critical game alerts (danger, starvation, idle colonists, ...), each at most once per 6h."""
        if time.time() - self._alerts_at < 5:
            return None
        self._alerts_at = time.time()
        try:
            alerts = self.bridge.call("state.alerts")
        except BridgeError:
            return None
        trigger = None
        live = set()
        for a in alerts:
            label = str(a.get("label", "")); pr = str(a.get("priority", ""))
            live.add(label)
            prios = set(self.cfg["play"].get("alert_wake_priorities", ["Critical"]))
            urgent = pr in prios or "idle" in label.lower()
            if not urgent:
                continue
            last = self._seen_alerts.get(label)
            if last is None or tick - last > float(self.cfg["play"].get("alert_rewake_hours", 24)) * TICKS_PER_HOUR:
                self._seen_alerts[label] = tick
                trigger = trigger or f"alert ({pr}): {label}"
        for label in list(self._seen_alerts):
            if label not in live:
                del self._seen_alerts[label]  # cleared alerts may wake again if they return
        return trigger

    def is_urgent(self, trigger: str) -> bool:
        t = trigger.lower()
        if t.startswith(("watcher alert", "alert (critical)", "operator")):
            return True
        return any(k in t for k in self.critical_kinds)

    def with_pause(self, fn, urgent: bool = False) -> None:
        # Calm steps think at think_speed (default: full play speed); urgent ones at danger_think_speed (default: paused).
        play = self.cfg["play"]
        think_speed = int(play.get("danger_think_speed", 0)) if urgent else int(play.get("think_speed", play.get("speed", 3)))
        try:
            if think_speed <= 0:
                self.bridge.call("game.pause", paused=True)
            else:
                self.bridge.call("game.speed", speed=think_speed)
                self.bridge.call("game.pause", paused=False)
        except BridgeError:
            pass
        self.thinking = True
        self.ctx.extra.pop("model_speed", None)
        self.bus.emit("status", {"phase": "thinking"})
        try:
            fn()
        finally:
            self.thinking = False
            # Restore play speed — unless the model chose one during the step (e.g. 1x for a raid). Never leave it paused.
            chosen = self.ctx.extra.get("model_speed")
            speed = int(play.get("speed", 3)) if chosen is None else max(1, int(chosen))
            try:
                self.bridge.call("game.speed", speed=speed)
                self.bridge.call("game.pause", paused=False)
            except BridgeError:
                pass
            self.bus.emit("status", {"phase": "playing", "model_speed": chosen})

    def play_step(self, trigger: str, tick: int) -> None:
        urgent = self.is_urgent(trigger)
        events, self.pending_events = self.pending_events, []
        alerts, self.pending_alerts = self.pending_alerts, []
        self.ctx.watcher_alerts = alerts
        extra = ""
        if self.operator_inbox:
            msgs, self.operator_inbox[:] = list(self.operator_inbox), []
            extra = ("## Message from the human operator\nAnswer it FIRST with the reply_to_operator tool (one or two sentences). "
                     "If it is a tip or instruction about how to play, LEARN it: edit the most relevant skill with skill_write so it says this from now on "
                     "(mark the line 'operator tip'), and act on it in the colony if it applies right now.\n" + "\n".join(f"- {m}" for m in msgs))
        msg, hint = situation_packet(self.ctx, trigger, events, alerts, extra=extra)
        res = think(self.ctx, msg, hint, trigger=trigger)
        self.step_notes.append(res.notes)
        play = self.cfg["play"]
        hours = self.ctx.wake.in_hours if self.ctx.wake.in_hours else float(play.get("wake_hours", 8))
        floor = 0.5 if (urgent or self.ctx.extra.get("model_speed") is not None) else float(play.get("min_wake_hours", 3))
        hours = max(floor, min(48.0, float(hours)))
        try:
            tick = int(self.bridge.status().get("tick", tick))
        except BridgeError:
            pass
        self._last_step_end_tick = tick
        self.next_wake_tick = tick + int(hours * TICKS_PER_HOUR)

    def run_improve(self, day: int) -> None:
        notes = reflect.improve(self.ctx, self.step_notes, day)
        self.step_notes.append(f"[improvement pass day {day}] {notes}")
        sha = braingit.commit(f"episode {self.episode} day {day}: improvement pass")
        if sha:
            self.bus.emit("brain_change", {"kind": "git", "action": "commit", "sha": sha})

    # ---------- episode end ----------
    def end_episode(self, reason: str) -> None:
        self.bus.emit("status", {"phase": "reflecting"})
        try:
            self.bridge.call("game.pause", paused=True)
        except BridgeError:
            pass
        st: dict[str, Any] = {}
        summary: dict[str, Any] = {}
        try:
            st = self.bridge.status()
            summary = self.bridge.call("state.summary")
        except BridgeError:
            pass
        days = int(st.get("day", self.last_day)) - self.start_day
        colonists = int(summary.get("colonists", st.get("colonists", 0)) or 0)
        assisted = bool(st.get("assisted", False))
        score = scorecard.score_from(days, colonists, self.deaths, float(summary.get("wealth", 0) or 0), float(summary.get("mood_avg", 0) or 0), int(summary.get("research_done", 0) or 0), self.raids)
        self.bus.emit("log", {"text": f"episode {self.episode} over: {reason}; days={days} colonists={colonists} deaths={self.deaths} score={score}"})
        notes = ""
        try:
            notes = reflect.episode(self.ctx, self.events, self.step_notes, reason, days)
        except Exception as e:  # noqa: BLE001
            self.bus.emit("error", {"text": f"reflection failed: {e}"})
        sha = braingit.commit(f"episode {self.episode} ({self.seed}): {reason}; score {score}\n\n{notes[:800]}") or braingit.head()
        scorecard.record({"episode": self.episode, "seed": self.seed, "days": days, "colonists": colonists, "deaths": self.deaths, "raids": self.raids, "wealth": summary.get("wealth"), "mood": summary.get("mood_avg"), "research": summary.get("research_done"), "score": score, "assisted": assisted, "brain_sha": sha, "ended": reason})
        self.bus.emit("episode_end", {"episode": self.episode, "score": score, "reason": reason, "assisted": assisted, "brain_sha": sha, "days": days})
        self.ctx.end_episode_reason = None
        self.seed = ""
        if self.stop:
            return
        self.new_game()


def start_dashboard(runner: Runner) -> threading.Thread | None:
    try:
        from .dashboard.app import create_app, serve_in_thread
    except Exception as e:  # noqa: BLE001
        runner.bus.emit("error", {"text": f"dashboard unavailable: {e}"})
        return None
    app = create_app(runner.bus, runner.bridge, runner.controls)
    port = int(runner.cfg.get("dashboard", {}).get("port", 8770))
    t = serve_in_thread(app, port)
    runner.bus.emit("log", {"text": f"dashboard at http://127.0.0.1:{port}"})
    return t
