"""Episode runner: keeps the game running, wakes the agent, scores episodes, starts the next game."""
from __future__ import annotations

import os
import subprocess
import webbrowser
import threading
import time
from typing import Any

from . import braingit, memory, reflect, scorecard
from . import watchdog as watchdog_mod
from .bridge import Bridge, BridgeError
from .bus import BUS, Bus
from .context import Context
from .llm import LLM
from .loop import ORDER_IDS, situation_packet, think
from .paths import ROOT, RUNS

EPISODE_FILE = RUNS / "episode.json"


def _save_episode(d: dict) -> None:
    try:
        import json
        EPISODE_FILE.write_text(json.dumps(d))
    except Exception:  # noqa: BLE001
        pass


def _load_episode() -> dict:
    try:
        import json
        return json.loads(EPISODE_FILE.read_text()) if EPISODE_FILE.exists() else {}
    except Exception:  # noqa: BLE001
        return {}
from .registry import Registry
from .tools import brain as brain_tools
from .tools import knowledge as knowledge_tools
from .tools import meta as meta_tools
from .tools import watchdog as watchdog_tools
from .watchers import run_all as run_watchers
from .watchers import superseded_mapping, superseded_now

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

    def set_parallel(self, value: bool):
        """Dashboard toggle: calm steps fan out to four specialist streams (econ, build, guard, caretaker)."""
        self.r.parallel = bool(value)
        self.r.bus.emit("log", {"text": "parallel mode ON: 4 specialist streams per calm step" if value else "parallel mode off: single stream"})
        self.r.bus.emit("status", {"parallel": bool(value)})

    @property
    def parallel(self) -> bool:
        return bool(self.r.parallel)

    def set_sandbox(self, value: bool):
        """Dashboard toggle: god mode (free instant builds) + all research; the run is marked assisted."""
        self.r.apply_sandbox(bool(value))

    @property
    def sandbox(self) -> bool:
        return bool(self.r.sandbox)

    def set_steward(self, value: bool):
        """Dashboard toggle: the in-mod steward (work-priority scorer + stock jobs). Off = the model micromanages again."""
        self.r.apply_steward(bool(value), announce=True)

    @property
    def steward(self) -> bool:
        return bool(self.r.steward)

    def set_order(self, order_id: str, value: bool):
        """Dashboard toggle: one standing order (or "all") on/off via steward.orders.set; remembered for the next new_game/recover."""
        return self.r.apply_order(str(order_id), bool(value), announce=True)

    def set_rally(self, rect: list[int] | None):
        """Dashboard: set the combat order's rally rect [x, z, w, h] (steward.orders.rally), or clear it with None."""
        return self.r.apply_rally(rect)

    @property
    def orders_off(self) -> list[str]:
        return sorted(self.r.orders_off)

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
        for mod in (knowledge_tools, brain_tools, meta_tools, watchdog_tools):
            self.registry.add_module(mod)
        self.ctx = Context(bridge=self.bridge, llm=self.llm, registry=self.registry, config=cfg, emit=self.bus.emit)
        self.controls = Controls(self)
        self.stop = False
        self.force_think: str | None = None
        self.force_end: str | None = None
        self.operator_inbox: list[str] = []
        self.ctx.extra["operator_inbox"] = self.operator_inbox  # shared with the loop so messages land mid-step
        self.ctx.interrupt_check = self.check_interrupts
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
        # watchdog stream: wall-clock cadence + an error-count gate, tracked against the bus sequence
        self._watchdog_at = time.time()
        self._watchdog_seq = 0
        self._watchdog_thread: threading.Thread | None = None
        self._watchdog_lock = threading.Lock()
        self.thinking = False
        self.sandbox = False
        self.parallel = bool(cfg["play"].get("parallel", False))
        self.steward = bool((cfg.get("steward") or {}).get("enabled", True))   # desired state; apply_steward pushes it to the mod
        ocfg = (cfg.get("steward") or {}).get("orders")
        ocfg = ocfg if isinstance(ocfg, dict) else {}
        self.orders_enabled = bool(ocfg.get("enabled", True))                  # config steward.orders.enabled; false = every standing order off
        self.orders_off: set[str] = {str(x) for x in (ocfg.get("off") or ocfg.get(False) or []) if x}   # order ids kept off (config + dashboard toggles); a bare `off:` key parses as False in YAML 1.1
        self.superseded_watchers = superseded_mapping(ocfg.get("superseded_watchers"))   # watcher stem -> order id that replaced it
        self.orders_supported: bool | None = None                              # None until the first steward.orders.set probe answers
        self._status_at = 0.0
        self._last_alive = time.time()
        self._alerts_at = 0.0
        self._seen_alerts: dict[str, int] = {}   # label -> tick last woken for it
        self._last_step_end_tick = 0
        self.critical_kinds = set(cfg["play"].get("critical_kinds", ["dialog", "danger", "manhunter", "hostile_group", "colonist_downed", "colonist_died", "mental_break", "building_lost"]))
        self.critical_kinds.discard("steward")   # steward ledger events (stock stalled/reached, posture expired) are ordinary wakes, never interrupts
        self.critical_kinds.discard("orders")    # standing-order events (combat engaged/released, rescue, corpses, fire) too: the runner already wakes on danger

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
                # Resumed into an existing game (agent restarted): adopt the game's god-mode state as the sandbox flag.
                self.sandbox = bool(st.get("god_mode", False))
                self.ctx.extra["sandbox"] = self.sandbox
                # Continue it as the current episode,
                # restoring the episode number / start day / counters saved by this same game if they match its seed.
                saved = _load_episode()
                self.seed = st.get("seed") or "resumed"
                if saved.get("seed") == self.seed:
                    self.episode = int(saved.get("episode", self.episode or 1))
                    self.start_day = int(saved.get("start_day", 0))
                    self.deaths = int(saved.get("deaths", 0)); self.raids = int(saved.get("raids", 0))
                    self.last_improve_day = int(saved.get("last_improve_day", self.start_day))
                else:
                    self.episode = max(self.episode, 1)
                    self.start_day = int(st.get("day", 0))
                    self.last_improve_day = self.start_day
                self.last_day = int(st.get("day", 0))
                self.ctx.last_seq = int(st.get("seq", 0))
                self.ctx.episode, self.ctx.seed = self.episode, self.seed
                self.bus.emit("episode_start", {"episode": self.episode, "seed": self.seed, "resumed": True, "day": self.start_day})
                self.apply_steward(self.steward)
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
        self.bus.emit("status", {"phase": "loading", "episode": self.episode, "seed": self.seed, "sandbox": self.sandbox})
        self.bridge.call("game.new_game", seed=self.seed, scenario=play.get("scenario", "Crashlanded"), storyteller=play.get("storyteller", "Cassandra"), difficulty=play.get("difficulty", "Rough"))
        time.sleep(3)
        st = self.bridge.wait_for("playing", 600)
        self.events, self.pending_events, self.pending_alerts, self.step_notes = [], [], [], []
        self.deaths = self.raids = 0
        self.start_day = self.last_day = int(st.get("day", 0))
        self.last_improve_day = self.start_day
        self.ctx.last_seq = 0
        self.ctx.episode, self.ctx.seed = self.episode, self.seed
        memory.notebook_reset(f"# Colony notebook, episode {self.episode}, seed {self.seed}\n\n(new game; nothing decided yet)")
        from . import tracker, worlddiff
        from .tools import meta as meta_tools_mod
        tracker.reset(); worlddiff.reset(); meta_tools_mod.reset_repl()
        if self.sandbox:
            self.apply_sandbox(True)
        self.apply_steward(self.steward)
        self.queue_default_research()
        self.bus.emit("episode_start", {"episode": self.episode, "seed": self.seed, "sandbox": self.sandbox})
        _save_episode({"seed": self.seed, "episode": self.episode, "start_day": self.start_day, "deaths": 0, "raids": 0, "last_improve_day": self.last_improve_day})
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
            script = ROOT / "script" / ("restart-game.ps1" if os.name == "nt" else "restart-game.sh")
            cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)] if os.name == "nt" else [str(script)]
            self.bus.emit("log", {"text": f"game unreachable for 60s; relaunching via script/{script.name}"})
            subprocess.run(cmd, timeout=60)
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
            self.apply_steward(self.steward)

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
                _save_episode({"seed": self.seed, "episode": self.episode, "start_day": self.start_day, "deaths": self.deaths, "raids": self.raids, "last_improve_day": self.last_improve_day})
                if play.get("autosave", True):
                    try:
                        self.bridge.call("game.save", name="rimagent-autosave")
                    except BridgeError as e:
                        self.bus.emit("error", {"text": f"autosave: {e}"})
                first = int(play.get("first_improve_day", 1))
                due = (day - self.start_day >= first and self.last_improve_day == self.start_day) or (day - self.last_improve_day >= int(play.get("improve_every_days", 3)))
                if due:
                    self.last_improve_day = day
                    self.start_improve_thread(day)
                self.maybe_start_watchdog(day)
            if self.controls.paused:
                time.sleep(1)
                continue
            trigger = self.wake_trigger(tick, new_events)
            if trigger:
                urgent = self.is_urgent(trigger)
                if self.parallel and not urgent:
                    self.with_pause(lambda: self.play_step_parallel(trigger, tick), urgent=False)
                else:
                    self.with_pause(lambda: self.play_step(trigger, tick), urgent=urgent)
                if self.ctx.end_episode_reason:
                    self.end_episode(self.ctx.end_episode_reason)
                    return
                continue
            time.sleep(0.5)

    def emit_status(self, st: dict[str, Any]) -> None:
        if time.time() - self._status_at < 1.0:
            return
        self._status_at = time.time()
        self.bus.emit("status", {**st, "episode": self.episode, "seed": self.seed, "phase": "thinking" if self.thinking else "playing", "deaths": self.deaths, "raids": self.raids, "next_wake_tick": self.next_wake_tick, "steward": self.steward})

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

    def check_interrupts(self) -> list[str]:
        """Called by the loop between tool calls: poll the ledger; return critical events (and pause the game for them)."""
        try:
            evs = self.poll_events()
        except BridgeError:
            return []
        urgent = [f"{e.get('kind')}: {e.get('text', '')}" for e in evs if e.get('kind') in self.critical_kinds]
        alerts = run_watchers(self.ctx, evs)
        urgent += [f"watcher {a.get('watcher')}: {a.get('text')}" for a in alerts if a.get("wake")]
        if urgent:
            try:
                self.bridge.call("game.pause", paused=True)
                self.ctx.extra["model_speed"] = None  # runner restores play speed after the step unless the model sets one
            except BridgeError:
                pass
        return urgent

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
                return f"event: {k}: {e.get('text', '')}"
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
        calm_speed = int(play.get("think_speed", play.get("speed", 3)))
        danger_speed = int(play.get("danger_think_speed", 0))
        think_speed = danger_speed if urgent else calm_speed
        # Recorded on this step's think_start. set_no_pause rewrites danger_think_speed at runtime, so the
        # config file does not say what a given step ran at.
        self.ctx.extra.update({"think_speed": think_speed, "config_think_speed": calm_speed,
                               "config_danger_think_speed": danger_speed, "urgent": bool(urgent)})
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
            for key in ("think_speed", "config_think_speed", "config_danger_think_speed", "urgent"):
                self.ctx.extra.pop(key, None)  # streams forked outside a step (improve, watchdog) must not copy them
            # Restore play speed, unless the model chose one during the step (e.g. 1x for a raid). Never leave it paused.
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
        self.ctx.extra["tick"] = tick
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
        self.ctx.extra["last_step_end_tick"] = tick
        self.next_wake_tick = tick + int(hours * TICKS_PER_HOUR)

    def apply_sandbox(self, on: bool) -> None:
        self.sandbox = on
        self.ctx.extra["sandbox"] = on
        try:
            self.bridge.call("game.dev_mode", enabled=True, god=on)
            if on:
                self.bridge.call("dev.unlock_all_research")
            self.bus.emit("log", {"text": "SANDBOX ON: god mode, all research unlocked; this run is marked assisted" if on else "sandbox off: god mode disabled (research stays unlocked)"})
            self.bus.emit("status", {"sandbox": on})
            self.force_think = "sandbox mode switched " + ("on: experiment and learn" if on else "off: play normally")
        except BridgeError as e:
            self.bus.emit("error", {"text": f"sandbox toggle failed: {e}"})

    def apply_steward(self, on: bool, announce: bool = False) -> dict[str, Any] | None:
        """Push the steward switches to the mod: steward.enable {scorer, stock} per config when on, both off otherwise.
        Defensive: a missing method or {ok:false} is logged and leaves the runner state as requested."""
        self.steward = bool(on)
        scfg = self.cfg.get("steward") or {}
        want = {"scorer": bool(on and scfg.get("scorer", True)), "stock": bool(on and scfg.get("stock", True))}
        result: dict[str, Any] | None = None
        try:
            got = self.bridge.call("steward.enable", **want)
            result = got if isinstance(got, dict) else want
            self.bus.emit("log", {"text": f"steward {'ON' if on else 'off'}: scorer={result.get('scorer', want['scorer'])} stock={result.get('stock', want['stock'])}"})
        except Exception as e:  # noqa: BLE001  (BridgeError for unknown method / ok:false, anything else if the bridge is down)
            self.bus.emit("error", {"text": f"steward.enable failed ({e}); the mod keeps its own default"})
        self.bus.emit("status", {"steward": self.steward})
        if announce:
            self.force_think = "steward switched " + ("on: direct it through rw_steward_* (targets, posture); stop setting priorities by hand" if on else "off: you set work priorities and designations yourself again")
        self.apply_orders()
        return result

    def apply_orders(self) -> dict[str, bool] | None:
        """Push config steward.orders to the mod (new_game / recover / steward toggle): steward.orders.set id=all enabled=true, then
        each id in `off` (and dashboard-disabled ids) off. Steward off or orders.enabled false = all orders off. Best effort:
        an older mod without steward.orders.set is logged once and ignored. Returns {id: enabled} as applied, or None."""
        all_on = bool(self.steward and self.orders_enabled)
        want: dict[str, bool] = {"all": all_on}
        if all_on:
            want.update({oid: False for oid in sorted(self.orders_off)})
        applied: dict[str, bool] = {}
        try:
            for oid, on in want.items():
                self.bridge.call("steward.orders.set", id=oid, enabled=on)
                applied[oid] = on
        except Exception as e:  # noqa: BLE001  (BridgeError for unknown method / unknown id, anything else if the bridge is down)
            self.bus.emit("error", {"text": f"steward.orders.set failed ({e}); the mod keeps its own order defaults"})
            self.orders_supported = False
            self.sync_superseded_watchers()
            return None
        self.orders_supported = True
        self.sync_superseded_watchers()
        off = sorted(k for k, v in applied.items() if not v and k != "all")
        self.bus.emit("log", {"text": "standing orders: " + ("all off" if not all_on else ("all on" if not off else "on except " + ", ".join(off)))})
        self.bus.emit("status", {"orders_off": sorted(self.orders_off), "orders_enabled": all_on})
        return applied

    def sync_superseded_watchers(self) -> dict[str, str]:
        """Skip the brain watchers a standing order replaced (registry.watcher_superseded) while that order is on and the mod
        answered steward.orders.set; an order switched off (or orders/steward off, or an older mod) hands its watcher back."""
        want = superseded_now(self.superseded_watchers, orders_supported=bool(self.orders_supported),
                              orders_on=bool(self.steward and self.orders_enabled), orders_off=self.orders_off)
        before = dict(self.registry.watcher_superseded)
        now = self.registry.set_superseded(want)
        if now != before:
            self.bus.emit("log", {"text": "superseded watchers (skipped while their order is on): " + (", ".join(f"{k}->{v}" for k, v in sorted(now.items())) or "none")})
        return now

    def apply_order(self, order_id: str, on: bool, announce: bool = False) -> dict[str, Any] | None:
        """One standing order (or "all") on/off now, remembered in orders_off for the next new_game/recover."""
        order_id = str(order_id or "").strip()
        if not order_id:
            raise ValueError("order id required")
        if order_id == "all":
            self.orders_off = set() if on else set(self.orders_off) | set(ORDER_IDS)
        elif on:
            self.orders_off.discard(order_id)
        else:
            self.orders_off.add(order_id)
        result: dict[str, Any] | None = None
        try:
            got = self.bridge.call("steward.orders.set", id=order_id, enabled=bool(on))
            result = got if isinstance(got, dict) else {"id": order_id, "enabled": bool(on)}
            self.bus.emit("log", {"text": f"standing order {order_id} {'ON' if on else 'off'}" + (f": {result['summary']}" if result.get("summary") else "")})
        except Exception as e:  # noqa: BLE001
            self.bus.emit("error", {"text": f"steward.orders.set {order_id} failed ({e})"})
        self.bus.emit("status", {"orders_off": sorted(self.orders_off)})
        if result is not None:
            self.orders_supported = True
        self.sync_superseded_watchers()
        if announce:
            self.force_think = f"standing order {order_id} switched " + ("on: the mod handles it again" if on else "off by the operator: you cover what it did yourself (rw_steward_orders_explain)")
        return result

    def apply_rally(self, rect: list[int] | None) -> dict[str, Any] | None:
        """steward.orders.rally {rect:[x,z,w,h]} or {clear:true}; returns the mod's answer ({rect} or null) or None on failure."""
        try:
            if rect is None:
                got = self.bridge.call("steward.orders.rally", clear=True)
            else:
                rect = [int(v) for v in list(rect)[:4]]
                if len(rect) != 4 or rect[2] <= 0 or rect[3] <= 0:
                    raise ValueError("rect must be [x, z, w, h] with w, h > 0")
                got = self.bridge.call("steward.orders.rally", rect=rect)
            self.bus.emit("log", {"text": "rally point " + ("cleared" if rect is None else f"set to {rect}")})
            return got if isinstance(got, dict) else ({"rect": rect} if rect is not None else None)
        except Exception as e:  # noqa: BLE001
            self.bus.emit("error", {"text": f"steward.orders.rally failed ({e})"})
            return None

    def queue_default_research(self) -> None:
        """config steward.research_queue_default -> steward.research {queue: [...]} on a new game (best effort)."""
        queue = list((self.cfg.get("steward") or {}).get("research_queue_default") or [])
        if not queue or not self.steward:
            return
        try:
            self.bridge.call("steward.research", queue=queue)
            self.bus.emit("log", {"text": "steward research queue: " + ", ".join(map(str, queue))})
        except Exception as e:  # noqa: BLE001
            self.bus.emit("error", {"text": f"steward.research failed ({e}); queue it yourself with rw_ui_set_research"})

    def usage_stats(self, since_seq: int | None = None) -> str:
        """Tool usage since the last improvement pass: counts, error rates, repeated call sequences (automation candidates)."""
        import collections
        since = since_seq if since_seq is not None else getattr(self, "_last_improve_seq", 0)
        evs = self.bus.since(since, limit=5000, kinds={"tool_call", "tool_result", "think_start"})
        self._last_improve_seq = self.bus.last_seq
        counts: collections.Counter = collections.Counter(); errs: collections.Counter = collections.Counter()
        seqs: collections.Counter = collections.Counter(); cur: list[str] = []
        for e in evs:
            d = e["data"]
            if e["kind"] == "think_start":
                cur = []
            elif e["kind"] == "tool_call":
                counts[d["name"]] += 1; cur.append(d["name"])
                if len(cur) >= 3 and not any(x in ("end_turn", "reply_to_operator") for x in cur[-3:]):
                    seqs[" > ".join(cur[-3:])] += 1
            elif e["kind"] == "tool_result" and not d.get("ok"):
                errs[d["name"]] += 1
        lines = ["Tool calls since the last pass (calls, errors):"]
        lines += [f"- {n}: {c}" + (f", {errs[n]} errors" if errs[n] else "") for n, c in counts.most_common(18)]
        rep = [(k, v) for k, v in seqs.most_common(8) if v >= 3]
        if rep:
            lines.append("Repeated call sequences (candidates for a tool or watcher):")
            lines += [f"- {k}  x{v}" for k, v in rep]
        return "\n".join(lines)

    def play_step_parallel(self, trigger: str, tick: int) -> None:
        """Fan a calm step out to the four specialist streams; merge notes and wake plans."""
        from . import roles as roles_mod
        self.ctx.extra["tick"] = tick
        events, self.pending_events = self.pending_events, []
        alerts, self.pending_alerts = self.pending_alerts, []
        self.ctx.watcher_alerts = alerts
        extra = ""
        if self.operator_inbox:
            msgs, self.operator_inbox[:] = list(self.operator_inbox), []
            extra = ("## Message from the human operator\nAnswer it FIRST with the reply_to_operator tool (one or two sentences). "
                     "If it is a tip or instruction about how to play, LEARN it: edit the most relevant skill with skill_write so it says this from now on "
                     "(mark the line 'operator tip'), and act on it in the colony if it applies right now.\n" + "\n".join(f"- {m}" for m in msgs))
        msg, hint = situation_packet(self.ctx, trigger, events, alerts)
        plan = self.manager_plan(msg, extra) if self.cfg["play"].get("parallel_manager", True) else {}
        directives = plan.get("directives") or {}
        skip = set(plan.get("skip") or [])
        if plan.get("wake_in_hours"):
            self.ctx.wake.in_hours = plan["wake_in_hours"]
        results: dict[str, Any] = {}

        def run(role: str):
            ctx_r = self.ctx.fork(role)
            ctx_r.extra["role"] = role
            if role == roles_mod.CARETAKER:
                ctx_r.extra["operator_inbox"] = self.operator_inbox
            if role == "guard":
                ctx_r.interrupt_check = self.check_interrupts
            directive = directives.get(role)
            user = msg + "\n\n" + roles_mod.brief_for(role) + (f"\n\n## Manager directive for you this step\n{directive}" if directive else "") + ("\n\n" + extra if role == roles_mod.CARETAKER and extra else "")
            try:
                results[role] = (ctx_r, think(ctx_r, user, hint + " " + role, trigger=f"{trigger} [{role}]", max_calls=int(self.cfg["play"].get("parallel_max_calls", 10)), tool_allow=roles_mod.allow_for(role), thinking=bool(self.cfg["play"].get("parallel_worker_thinking", False))))
            except Exception as e:  # noqa: BLE001
                self.bus.emit("error", {"text": f"{role} stream failed: {e}"})

        active = [r for r in roles_mod.ROLES if r not in skip] or [roles_mod.CARETAKER]
        if skip:
            self.bus.emit("log", {"text": "manager skipped streams this step: " + ", ".join(sorted(skip))})
        threads = [threading.Thread(target=run, args=(r,), name=f"stream-{r}", daemon=True) for r in active]
        for t in threads:
            t.start()
        for t in threads:
            t.join(timeout=900)
        notes = []; hours = []; end_reason = None
        for role, (ctx_r, res) in results.items():
            if res.notes:
                notes.append(f"[{role}] {res.notes}")
            if ctx_r.wake.in_hours:
                hours.append(float(ctx_r.wake.in_hours))
            if ctx_r.end_episode_reason:
                end_reason = ctx_r.end_episode_reason
            if ctx_r.extra.get("model_speed") is not None:
                self.ctx.extra["model_speed"] = ctx_r.extra["model_speed"]
        self.step_notes.append(" | ".join(notes))
        if end_reason:
            self.ctx.end_episode_reason = end_reason
        play = self.cfg["play"]
        h = float(plan.get("wake_in_hours") or (min(hours) if hours else float(play.get("wake_hours", 8))))
        h = max(float(play.get("min_wake_hours", 3)), min(48.0, h))
        try:
            tick = int(self.bridge.status().get("tick", tick))
        except BridgeError:
            pass
        self._last_step_end_tick = tick
        self.ctx.extra["last_step_end_tick"] = tick
        self.next_wake_tick = tick + int(h * TICKS_PER_HOUR)

    def manager_plan(self, packet: str, extra: str = "") -> dict[str, Any]:
        """Manager-lite: one fast planning call (no tools, thinking off) that assigns directives to the specialist streams."""
        import json as _json
        from . import roles as roles_mod
        self.bus.emit("think_start", {"trigger": "manager plan", "stream": "manager", "prompt_chars": len(packet), "tools": 0})
        t0 = time.time()
        system = ("You are the colony manager for an autonomous RimWorld agent. Four specialist streams act in parallel this step: "
                  + "; ".join(f"{k} = {v['title']}: {v['brief'].split('.')[0]}" for k, v in roles_mod.ROLES.items())
                  + ". Read the situation and reply with ONLY a JSON object: {\"priorities\": [\"...\" up to 3, most urgent first], "
                  "\"directives\": {\"econ\": \"one or two sentences of concrete orders, or empty string\", \"build\": \"...\", \"guard\": \"...\", \"caretaker\": \"...\"}, "
                  "\"skip\": [roles with nothing worth doing this step], \"wake_in_hours\": number (3-24; low when something is developing)}. "
                  "Be specific: name pawns, places, quantities. Resolve conflicts between roles here (e.g. who gets the wood). Never skip caretaker when there are open dialogs, letters or an operator message. "
                  "The in-game steward already sets work priorities and designates trees/ore/animals toward stock targets: direct it (targets, posture) rather than ordering per-pawn priorities.")
        try:
            reply = self.llm.chat([{"role": "system", "content": system}, {"role": "user", "content": packet + ("\n\n" + extra if extra else "")}], tools=None, thinking=False, max_tokens=900, temperature=0.3)
            text = reply.content.strip()
            start, end = text.find("{"), text.rfind("}")
            plan = _json.loads(text[start:end + 1]) if start >= 0 and end > start else {}
            if not isinstance(plan, dict):
                plan = {}
        except Exception as e:  # noqa: BLE001
            self.bus.emit("error", {"text": f"manager plan failed: {e}"})
            plan = {}
        self.bus.emit("assistant", {"text": _json.dumps(plan, ensure_ascii=False, indent=1), "stream": "manager"})
        self.bus.emit("think_end", {"notes": "; ".join(plan.get("priorities") or []) or "(no plan)", "wake": {"in_hours": plan.get("wake_in_hours")}, "calls": 0, "elapsed": round(time.time() - t0, 1), "stream": "manager"})
        return plan

    def start_improve_thread(self, day: int) -> None:
        """Improvement pass on a second LLM stream, concurrent with play (brain edits hot-load into the play stream)."""
        if getattr(self, "_improve_thread", None) and self._improve_thread.is_alive():
            return
        ctx2 = self.ctx.fork("improve")
        notes_snapshot = list(self.step_notes)
        ctx2.extra["usage_stats"] = self.usage_stats()

        def run():
            try:
                notes = reflect.improve(ctx2, notes_snapshot, day)
                self.step_notes.append(f"[improvement pass day {day}] {notes}")
                sha = braingit.commit(f"episode {self.episode} day {day}: improvement pass")
                if sha:
                    self.bus.emit("brain_change", {"kind": "git", "action": "commit", "sha": sha})
            except Exception as e:  # noqa: BLE001
                self.bus.emit("error", {"text": f"improvement pass failed: {e}"})

        self._improve_thread = threading.Thread(target=run, name="improve", daemon=True)
        self._improve_thread.start()
        self.bus.emit("log", {"text": f"improvement pass started on a second stream (day {day})"})

    # ---------- watchdog ----------
    def watchdog_errors(self) -> list[dict[str, Any]]:
        """Failed tool calls recorded on the bus since the last watchdog pass (the pass's whole input)."""
        return watchdog_mod.recent_errors(self.bus, self._watchdog_seq)

    def maybe_start_watchdog(self, day: int) -> None:
        """Start a watchdog pass if the cadence AND the error threshold are both met (config `watchdog`).

        Called on every in-game day rollover, which is just a cheap regular tick: the real gate is
        `watchdog.due()`, which needs `every_hours` of wall clock AND `min_errors` failures since the last pass.
        A clean error stream never triggers one."""
        if self._watchdog_thread is not None and self._watchdog_thread.is_alive():
            return
        improving = getattr(self, "_improve_thread", None)
        if improving is not None and improving.is_alive():
            return   # one brain-level pass at a time; they share the LLM and the repo's git index
        errors = self.watchdog_errors()
        ok, why = watchdog_mod.due(self.cfg, self._watchdog_at, len(errors))
        if not ok:
            return
        self.start_watchdog_thread(day, errors, why)

    def start_watchdog_thread(self, day: int, errors: list[dict[str, Any]] | None = None, why: str = "") -> None:
        """Run the watchdog on its own stream, concurrent with play. It touches mod/Source and agent/rimagent only,
        so it cannot collide with the improvement pass (brain/) or with the running game: nothing it writes is loaded
        until a human restarts."""
        with self._watchdog_lock:
            if self._watchdog_thread is not None and self._watchdog_thread.is_alive():
                return
            errors = self.watchdog_errors() if errors is None else errors
            self._watchdog_at = time.time()
            self._watchdog_seq = self.bus.last_seq
            wcfg = self.cfg.get("watchdog") or {}
            max_calls = int(wcfg.get("max_tool_calls", 40))
            ctx2 = self.ctx.fork("watchdog")
            ctx2.extra["watchdog_state"] = watchdog_mod.PassState()

            def run():
                try:
                    watchdog_mod.run_pass(ctx2, errors, max_calls=max_calls)
                except Exception as e:  # noqa: BLE001
                    self.bus.emit("error", {"text": f"watchdog pass failed: {e}"})

            self._watchdog_thread = threading.Thread(target=run, name="watchdog", daemon=True)
            self._watchdog_thread.start()
            self.bus.emit("log", {"text": f"watchdog pass started on its own stream (day {day}, {len(errors)} errors; {why})"})

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
        assisted = bool(st.get("assisted", False)) or self.sandbox
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
    dash = runner.cfg.get("dashboard", {})
    port = int(dash.get("port", 8770))
    t = serve_in_thread(app, port)
    url = f"http://127.0.0.1:{port}"
    runner.bus.emit("log", {"text": f"dashboard at {url}"})
    if dash.get("open_browser", True):
        # script/start.sh did this with macOS `open`; webbrowser works on every
        # platform and does not require launching the agent through that script.
        def _open() -> None:
            time.sleep(2)  # let uvicorn bind before the tab races it
            try:
                webbrowser.open(url)
            except Exception as e:  # noqa: BLE001
                runner.bus.emit("log", {"text": f"could not open a browser ({e}); visit {url}"})
        threading.Thread(target=_open, daemon=True, name="dashboard-open").start()
    return t
