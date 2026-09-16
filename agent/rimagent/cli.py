"""rimagent command line."""
from __future__ import annotations

import argparse
import json
import sys

from .config import CONFIG


def cmd_seed(args):
    from .knowledge import wiki
    if not args.skip_wiki:
        wiki.scrape(force=args.force, log=print)
    if args.distill:
        from .knowledge import seed
        from .llm import LLM
        seed.distill_skills(LLM(), print)
    print("seed done")


def cmd_play(args):
    from .bus import BUS
    from .runner import Runner, start_dashboard
    if args.max_days:
        CONFIG["play"]["max_days"] = args.max_days
    if args.seeds:
        CONFIG["play"]["seeds"] = args.seeds.split(",")
    if args.no_pause:
        CONFIG["play"]["pause_to_think"] = False
    r = Runner(CONFIG, BUS)
    if not args.no_dashboard:
        start_dashboard(r)
    if args.verbose:
        def printer(ev):
            k, d = ev["kind"], ev["data"]
            if k in ("tool_call",):
                print(f"→ {d['name']} {json.dumps(d['args'])[:300]}")
            elif k == "tool_result":
                print(f"← {d['name']} {'ok' if d['ok'] else 'ERR'} {d['text'][:300]}")
            elif k in ("assistant", "log", "error", "think_start", "think_end", "episode_start", "episode_end", "watcher", "brain_change"):
                print(f"[{k}] {json.dumps(d, default=str)[:600]}")
            elif k == "reasoning":
                print(f"[thinking] {d['text'][:400].replace(chr(10), ' ')}…")
        BUS.subscribe(printer)
    try:
        r.run()
    except KeyboardInterrupt:
        r.stop = True
        print("stopping")


def cmd_think(args):
    """One play step against the live game, then exit (for debugging)."""
    from .bus import BUS
    from .loop import situation_packet, think
    from .runner import Runner
    r = Runner(CONFIG, BUS)
    r.bridge.wait_alive(30)
    r.registry.add_bridge_methods(r.bridge.methods())
    r.registry.reload_brain()
    BUS.subscribe(lambda ev: print(f"[{ev['kind']}] {json.dumps(ev['data'], default=str)[:800]}"))
    st = r.bridge.status()
    if st.get("state") != "playing":
        print("no game running; use `rimagent play` or start one via the bridge")
        return
    r.bridge.call("game.pause", paused=True)
    events = r.bridge.events(max(0, int(st.get("seq", 0)) - 50)).get("events", [])
    msg, hint = situation_packet(r.ctx, args.trigger, events, [])
    res = think(r.ctx, msg, hint, trigger=args.trigger)
    print("\nNOTES:", res.notes, "\ncalls:", res.calls, "elapsed:", round(res.elapsed, 1))


def cmd_tools(args):
    from .bridge import Bridge
    from .registry import Registry
    from .tools import brain, knowledge, meta
    reg = Registry()
    for m in (knowledge, brain, meta):
        reg.add_module(m)
    b = Bridge()
    if b.health():
        reg.add_bridge_methods(b.methods())
    reg.reload_brain()
    for t in sorted(reg.tools.values(), key=lambda t: (t.source, t.name)):
        print(f"{t.source:8s} {t.group:10s} {t.name:34s} {t.description[:90]}")
    if reg.load_errors:
        print("load errors:", reg.load_errors)


def cmd_llm(args):
    from .llm import LLM
    r = LLM().chat([{"role": "user", "content": args.prompt}], thinking=not args.no_thinking, max_tokens=400)
    print("reasoning:", r.reasoning[:500])
    print("content:", r.content)
    print("usage:", r.usage, "elapsed:", round(r.elapsed, 1))


def main(argv=None):
    import faulthandler, signal
    faulthandler.register(signal.SIGUSR1, all_threads=True)  # kill -USR1 <pid> dumps every thread's stack to stderr
    ap = argparse.ArgumentParser(prog="rimagent")
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("seed", help="scrape wiki, build indexes, distill starter skills")
    s.add_argument("--skip-wiki", action="store_true")
    s.add_argument("--force", action="store_true")
    s.add_argument("--distill", action="store_true", help="ask the LLM to write starter skills from wiki pages")
    s.set_defaults(fn=cmd_seed)
    p = sub.add_parser("play", help="run episodes unattended")
    p.add_argument("--max-days", type=int)
    p.add_argument("--seeds")
    p.add_argument("--no-pause", action="store_true")
    p.add_argument("--no-dashboard", action="store_true")
    p.add_argument("-v", "--verbose", action="store_true")
    p.set_defaults(fn=cmd_play)
    t = sub.add_parser("think", help="run one think step against the live game")
    t.add_argument("--trigger", default="manual")
    t.set_defaults(fn=cmd_think)
    sub.add_parser("tools", help="list tools").set_defaults(fn=cmd_tools)
    l = sub.add_parser("llm", help="test the LLM endpoint")
    l.add_argument("prompt")
    l.add_argument("--no-thinking", action="store_true")
    l.set_defaults(fn=cmd_llm)
    args = ap.parse_args(argv)
    args.fn(args)


if __name__ == "__main__":
    main()
