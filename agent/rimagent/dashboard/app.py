"""Web dashboard for rimagent: live transcript, game ledger, watchers, brain browser, scores, map.

    app = create_app(BUS, bridge, controls)
    serve_in_thread(app, CONFIG["dashboard"]["port"])

Standalone demo (dummy bus emitting sample events, no game needed):

    cd agent && uv run python -m rimagent.dashboard.app      # then open http://localhost:8770
"""
from __future__ import annotations

import asyncio
import json
import threading
import time
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse, Response
from pydantic import BaseModel
from sse_starlette.sse import EventSourceResponse

from .. import braingit, scorecard, skills
from ..paths import JOURNAL, NOTEBOOK, SKILLS, TOOLS, WATCHERS

_KIND_DIRS: dict[str, Path] = {"skill": SKILLS, "tool": TOOLS, "watcher": WATCHERS}
_CONTROL_ACTIONS = {"pause": "pause", "resume": "resume", "think": "think_now", "end_episode": "end_episode", "no_pause": "set_no_pause", "kill": "kill"}


class ControlRequest(BaseModel):
    action: str
    value: bool | None = None


def _brain_file(kind: str, name: str | None) -> Path:
    """Map (kind, name) to a file under brain/, refusing anything outside the expected directory."""
    if kind == "notebook":
        return NOTEBOOK
    if kind == "journal":
        return JOURNAL
    base = _KIND_DIRS.get(kind)
    if base is None:
        raise HTTPException(400, f"unknown kind {kind!r}")
    if not name or "/" in name or "\\" in name or ".." in name or name.startswith("."):
        raise HTTPException(400, "bad name")
    base_r = base.resolve()
    p = (base_r / name).resolve()
    if p.parent != base_r:
        raise HTTPException(400, "bad name")
    if kind == "skill" and not p.exists():
        for s in skills.load_all():
            if s.name == name or s.path.stem == name:
                p = s.path.resolve()
                break
        else:
            alt = p.with_suffix(".md")
            if alt.parent == base_r and alt.exists():
                p = alt
    if not p.is_file():
        raise HTTPException(404, f"no {kind} {name!r}")
    return p


def _list_dir(d: Path) -> list[str]:
    try:
        return sorted(p.name for p in d.iterdir() if p.is_file() and not p.name.startswith(".") and p.suffix != ".pyc")
    except OSError:
        return []


def _chars(p: Path) -> int:
    try:
        return len(p.read_text(encoding="utf-8"))
    except OSError:
        return 0


async def _call_with_timeout(fn, *args: Any, timeout: float = 8.0, **kw: Any) -> Any:
    return await asyncio.wait_for(asyncio.to_thread(fn, *args, **kw), timeout)


def create_app(bus: Any, bridge: Any, controls: Any) -> FastAPI:
    app = FastAPI(title="rimagent dashboard", docs_url=None, redoc_url=None)

    @app.get("/", response_class=HTMLResponse)
    def index() -> str:
        return PAGE

    @app.get("/api/events")
    def api_events(since: int = 0, kinds: str | None = None, limit: int = Query(500, ge=1, le=5000)) -> list[dict[str, Any]]:
        ks = {k.strip() for k in kinds.split(",") if k.strip()} if kinds else None
        return bus.since(since, limit=limit, kinds=ks)

    @app.get("/stream")
    async def stream(request: Request, since: int = 0):
        last_id = request.headers.get("last-event-id")
        if last_id and last_id.isdigit():
            since = int(last_id)

        async def gen():
            seq = since
            # Hydrate anything the client missed between its /api/events call and this connect.
            for e in bus.since(seq, limit=5000):
                seq = e["seq"]
                yield {"id": str(seq), "data": json.dumps(e, default=str)}
            while True:
                if await request.is_disconnected():
                    break
                evs = await asyncio.to_thread(bus.wait, seq, 5.0)
                for e in evs:
                    if e["seq"] > seq:
                        seq = e["seq"]
                        yield {"id": str(seq), "data": json.dumps(e, default=str)}

        return EventSourceResponse(gen(), ping=15)

    @app.get("/api/state")
    async def api_state() -> dict[str, Any]:
        game: Any = None
        try:
            game = await _call_with_timeout(bridge.status, timeout=5.0)
        except Exception:  # noqa: BLE001
            game = None
        ctl = {"paused": bool(getattr(controls, "paused", False)), "no_pause": getattr(controls, "no_pause", None)}
        return {"bus": bus.state, "last_seq": bus.last_seq, "game": game, "controls": ctl}

    @app.get("/api/brain/tree")
    def api_brain_tree() -> dict[str, Any]:
        try:
            sk = [{"name": s.name, "description": s.description, "chars": s.chars, "always": s.always, "file": s.path.name} for s in skills.load_all()]
        except Exception:  # noqa: BLE001
            sk = []
        return {
            "skills": sk,
            "tools": _list_dir(TOOLS),
            "watchers": _list_dir(WATCHERS),
            "notebook_chars": _chars(NOTEBOOK),
            "journal_chars": _chars(JOURNAL),
        }

    @app.get("/api/brain/file")
    def api_brain_file(kind: str, name: str | None = None) -> dict[str, Any]:
        p = _brain_file(kind, name)
        try:
            text = p.read_text(encoding="utf-8")
        except OSError as e:
            raise HTTPException(404, str(e)) from e
        return {"kind": kind, "name": p.name, "text": text}

    @app.get("/api/git/log", response_class=PlainTextResponse)
    async def api_git_log(n: int = Query(30, ge=1, le=500)) -> str:
        try:
            return await _call_with_timeout(braingit.log, n, timeout=20.0)
        except Exception as e:  # noqa: BLE001
            return f"(error: {e})"

    @app.get("/api/git/diff", response_class=PlainTextResponse)
    async def api_git_diff(sha: str) -> str:
        if not sha or not all(c.isalnum() for c in sha) or len(sha) > 64:
            raise HTTPException(400, "bad sha")
        try:
            return await _call_with_timeout(braingit.diff, sha, timeout=20.0)
        except Exception as e:  # noqa: BLE001
            return f"(error: {e})"

    @app.get("/api/scores")
    def api_scores() -> list[dict[str, Any]]:
        try:
            return scorecard.history(100)
        except Exception:  # noqa: BLE001
            return []

    @app.get("/api/ledger")
    async def api_ledger(since: int = 0) -> dict[str, Any]:
        try:
            return await _call_with_timeout(bridge.events, since, timeout=10.0)
        except Exception as e:  # noqa: BLE001
            return {"events": [], "error": str(e)}

    @app.get("/api/ascii")
    async def ascii_view(x: int | None = None, z: int | None = None, w: int = 80, h: int = 50, layer: str = "all"):
        params: dict[str, Any] = {"w": w, "h": h, "layer": layer}
        if x is not None:
            params["x"] = x
        if z is not None:
            params["z"] = z
        try:
            return await _call_with_timeout(lambda: bridge.call("map.view", **params), timeout=20.0)
        except Exception as e:  # noqa: BLE001
            return JSONResponse({"error": str(e)}, status_code=503)

    @app.get("/api/overview")
    async def overview(blocks: int = 60):
        try:
            return await _call_with_timeout(lambda: bridge.call("map.overview", blocks=blocks), timeout=20.0)
        except Exception as e:  # noqa: BLE001
            return JSONResponse({"error": str(e)}, status_code=503)

    @app.get("/screenshot.png")
    async def screenshot(x: int | None = None, z: int | None = None, w: float = 80):
        try:
            png = await _call_with_timeout(bridge.screenshot, x, z, w, timeout=35.0)
        except Exception as e:  # noqa: BLE001
            return JSONResponse({"error": str(e)}, status_code=503)
        return Response(content=png, media_type="image/png", headers={"Cache-Control": "no-store"})

    @app.post("/api/say")
    async def say(req: Request):
        body = await req.json()
        text = str(body.get("text", "")).strip()
        if not text:
            return JSONResponse({"ok": False, "error": "empty"}, status_code=400)
        fn = getattr(controls, "say", None)
        if not callable(fn):
            return JSONResponse({"ok": False, "error": "runner has no say()"}, status_code=501)
        fn(text)
        return {"ok": True}

    @app.post("/api/control")
    def api_control(req: ControlRequest) -> dict[str, Any]:
        method = _CONTROL_ACTIONS.get(req.action)
        if method is None:
            raise HTTPException(400, f"unknown action {req.action!r}")
        fn = getattr(controls, method, None)
        if not callable(fn):
            return {"ok": False, "error": f"controls has no {method}()", "paused": bool(getattr(controls, "paused", False))}
        try:
            result = fn(bool(req.value)) if req.action == "no_pause" else fn()
        except Exception as e:  # noqa: BLE001
            return {"ok": False, "error": str(e), "paused": bool(getattr(controls, "paused", False))}
        return {"ok": True, "result": result if isinstance(result, (str, int, float, bool, dict, list)) or result is None else str(result), "paused": bool(getattr(controls, "paused", False)), "no_pause": getattr(controls, "no_pause", None)}

    return app


def serve_in_thread(app: FastAPI, port: int, host: str = "127.0.0.1") -> threading.Thread:
    import uvicorn

    config = uvicorn.Config(app, host=host, port=port, log_level="warning", access_log=False)
    server = uvicorn.Server(config)
    t = threading.Thread(target=server.run, name="dashboard", daemon=True)
    t.start()
    return t


# --------------------------------------------------------------------------------------
# Single-page UI
# --------------------------------------------------------------------------------------

PAGE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>rimagent</title>
<link rel="icon" href="data:,">
<style>
:root{
  --bg:#0e1116;--bg1:#151a21;--bg2:#1c222b;--bg3:#242c37;--line:#2b3340;
  --fg:#d7dde6;--fg1:#9aa5b4;--fg2:#6b7684;
  --acc:#5aa9ff;--ok:#4fc37a;--warn:#e6b450;--err:#ef6a6a;--pur:#b48bf2;--teal:#4ec9c9;
  --mono:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
  --sans:-apple-system,BlinkMacSystemFont,"Segoe UI",Inter,Roboto,sans-serif;
}
*{box-sizing:border-box}
html,body{height:100%;margin:0}
body{background:var(--bg);color:var(--fg);font:12.5px/1.45 var(--sans);display:flex;flex-direction:column;overflow:hidden}
a{color:var(--acc)}
button{background:var(--bg3);color:var(--fg);border:1px solid var(--line);border-radius:4px;padding:3px 9px;font:inherit;cursor:pointer}
button:hover{background:#2c3644;border-color:#3a4557}
button.primary{background:#1f3b5e;border-color:#2d5687}
button.danger{background:#4a2323;border-color:#6d3030}
button:disabled{opacity:.5;cursor:default}
input,select{background:var(--bg1);color:var(--fg);border:1px solid var(--line);border-radius:4px;padding:3px 6px;font:inherit}
input[type=number]{width:70px}
pre{margin:0;font:11.5px/1.4 var(--mono);white-space:pre-wrap;word-break:break-word}
code{font-family:var(--mono);background:var(--bg3);padding:0 3px;border-radius:3px}
.mono{font-family:var(--mono)}
.dim{color:var(--fg1)}.dimmer{color:var(--fg2)}
.badge{display:inline-block;padding:0 6px;border-radius:3px;font:10.5px/17px var(--mono);letter-spacing:.2px;text-transform:uppercase;background:var(--bg3);color:var(--fg1);white-space:nowrap}
.badge.ok{background:#193a27;color:var(--ok)}.badge.err{background:#3d1d1d;color:var(--err)}
.badge.warn{background:#3d321a;color:var(--warn)}.badge.info{background:#1a2c44;color:var(--acc)}
.badge.pur{background:#2c2242;color:var(--pur)}.badge.teal{background:#173636;color:var(--teal)}

/* status strip */
#strip{display:flex;align-items:center;gap:14px;padding:6px 12px;background:var(--bg1);border-bottom:1px solid var(--line);flex-wrap:wrap}
#strip .brand{font-weight:600;letter-spacing:.5px;color:#fff;margin-right:4px}
#strip .stat{display:flex;flex-direction:column;min-width:52px}
#strip .stat label{font-size:9.5px;text-transform:uppercase;letter-spacing:.6px;color:var(--fg2)}
#strip .stat span{font-family:var(--mono);font-size:12.5px;white-space:nowrap}
#strip .phase span{padding:0 6px;border-radius:3px;background:var(--bg3)}
#strip .phase.thinking span{background:#1a2c44;color:var(--acc)}
#strip .phase.playing span{background:#193a27;color:var(--ok)}
#strip .phase.reflecting span{background:#2c2242;color:var(--pur)}
#strip .phase.loading span{background:#3d321a;color:var(--warn)}
#strip .controls{margin-left:auto;display:flex;gap:6px;align-items:center}
#agentpaused{display:none;color:var(--warn);font-weight:600}
#agentpaused.on{display:inline}
#conn{width:8px;height:8px;border-radius:50%;background:var(--err);display:inline-block;margin-right:4px}
#conn.on{background:var(--ok)}

/* tabs */
#tabs{display:flex;gap:2px;padding:4px 12px 0;background:var(--bg1);border-bottom:1px solid var(--line)}
#tabs button{border:1px solid transparent;border-bottom:none;background:transparent;color:var(--fg1);padding:5px 12px;border-radius:5px 5px 0 0}
#tabs button.active{background:var(--bg);color:#fff;border-color:var(--line)}
#tabs button .cnt{font:10px var(--mono);color:var(--fg2);margin-left:4px}
main{flex:1;min-height:0;display:flex}
main>.tab{flex:1;min-height:0;min-width:0;display:flex}
main>.tab:not(.active){display:none}
.toolbar{display:flex;gap:8px;align-items:center;padding:6px 12px;border-bottom:1px solid var(--line);background:var(--bg1);flex-wrap:wrap}
.scroll{flex:1;min-height:0;overflow:auto;padding:8px 12px}
.col{display:flex;flex-direction:column;min-height:0;min-width:0}

/* live transcript */
#live .step{border:1px solid var(--line);border-radius:6px;margin:0 0 10px;background:var(--bg1)}
#live .step-head{display:flex;gap:10px;align-items:center;padding:5px 10px;background:var(--bg2);border-radius:6px 6px 0 0;cursor:pointer;font-family:var(--mono);font-size:11.5px}
#live .step-head .trig{color:var(--acc)}
#live .step-head .sum{color:var(--fg2);margin-left:auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#live .step.collapsed .step-body{display:none}
#live .step.collapsed .step-head{border-radius:6px}
#live .step-body{padding:6px 10px}
#live .item{margin:5px 0}
#live details.reasoning{border-left:2px solid var(--bg3);padding-left:8px}
#live details.reasoning summary{cursor:pointer;color:var(--fg2);font-style:italic;user-select:none}
#live details.reasoning pre{color:var(--fg2);margin-top:4px;max-height:420px;overflow:auto}
#live .assistant{white-space:pre-wrap;padding:6px 10px;background:var(--bg2);border-radius:5px;border-left:2px solid var(--acc)}
#live .tool{border:1px solid var(--line);border-radius:5px;background:var(--bg)}
#live .tool-head{display:flex;gap:8px;align-items:center;padding:4px 8px;font-family:var(--mono);font-size:11.5px}
#live .tool-head .tname{color:var(--teal);font-weight:600}
#live .tool-head .argprev{color:var(--fg2);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0}
#live .tool-head .el{color:var(--fg2)}
#live .tool pre.args{padding:4px 8px 6px;border-top:1px solid var(--line);color:var(--fg1)}
#live .tool .result{border-top:1px solid var(--line);padding:4px 8px}
#live .tool .result pre{color:var(--fg1)}
#live .tool .result .more{color:var(--acc);cursor:pointer;font-size:11px;margin-left:6px}
#live .tool.pending .tool-head{opacity:.75}
#live .think-end{background:#141c26;border:1px dashed #2a3b52;border-radius:5px;padding:5px 10px;font-size:12px}
#live .think-end .wake{font-family:var(--mono);color:var(--fg1);font-size:11px;margin-top:3px}
#live .sys{font-family:var(--mono);font-size:11.5px;color:var(--fg1);padding:2px 4px;margin:2px 0}
#live .sys.error{color:var(--err)}
#live .sys.episode{color:var(--warn)}
.empty{color:var(--fg2);padding:20px;text-align:center}

/* rows (ledger/watchers) */
.rows .row{display:flex;gap:10px;align-items:baseline;padding:3px 4px;border-bottom:1px solid #1a2029;font-size:12px}
.rows .row:hover{background:var(--bg1)}
.rows .when{font-family:var(--mono);color:var(--fg2);min-width:64px;white-space:nowrap}
.rows .text{flex:1;min-width:0;word-break:break-word}
.rows .extra{font-family:var(--mono);color:var(--fg2);font-size:11px}
.rows .badge{min-width:90px;text-align:center}
.rows .row.hidden{display:none}

/* brain */
#brain-left{width:300px;border-right:1px solid var(--line);flex:none}
#brain-tree h4{margin:10px 0 4px;font-size:10.5px;text-transform:uppercase;letter-spacing:.6px;color:var(--fg2)}
#brain-tree .node{padding:2px 6px;border-radius:3px;cursor:pointer;display:flex;gap:6px;align-items:baseline}
#brain-tree .node:hover{background:var(--bg2)}
#brain-tree .node.sel{background:#1a2c44}
#brain-tree .node .n{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
#brain-tree .node .c{font:10px var(--mono);color:var(--fg2)}
#brain-tree .node .d{display:block;font-size:11px;color:var(--fg2);white-space:normal}
#brain-view{flex:1;min-width:0}
#brain-title{font-family:var(--mono);color:var(--fg1)}
.md h1,.md h2,.md h3,.md h4{margin:12px 0 4px;color:#fff;font-weight:600}
.md h1{font-size:16px}.md h2{font-size:14px}.md h3{font-size:13px}.md h4{font-size:12.5px}
.md p{margin:4px 0}.md ul,.md ol{margin:4px 0 4px 18px;padding:0}
.md pre{background:var(--bg1);border:1px solid var(--line);border-radius:5px;padding:6px 8px;margin:6px 0}
.md hr{border:0;border-top:1px solid var(--line)}
#git-log .line{font-family:var(--mono);font-size:11.5px;padding:2px 6px;cursor:pointer;border-radius:3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#git-log .line:hover{background:var(--bg2)}
#git-log .line.sel{background:#1a2c44}
#git-log .line .sha{color:var(--warn)}
#git-diff{background:var(--bg1);border:1px solid var(--line);border-radius:5px;padding:6px 8px;max-height:50vh;overflow:auto}
#git-diff .add{color:var(--ok)}#git-diff .del{color:var(--err)}#git-diff .hunk{color:var(--acc)}#git-diff .meta{color:var(--fg2)}
#brain-feed .row{font-size:11.5px}
.split{display:flex;flex:1;min-height:0}
.section-h{font-size:10.5px;text-transform:uppercase;letter-spacing:.6px;color:var(--fg2);margin:10px 0 4px}

/* scores */
table{border-collapse:collapse;width:100%;font-size:12px}
th,td{padding:3px 8px;text-align:left;border-bottom:1px solid #1a2029;font-family:var(--mono);white-space:nowrap}
th{color:var(--fg2);font-weight:500;font-size:10.5px;text-transform:uppercase;letter-spacing:.5px;position:sticky;top:0;background:var(--bg)}
tr:hover td{background:var(--bg1)}
#scores-chart{width:100%;max-width:900px;height:auto;display:block;background:var(--bg1);border:1px solid var(--line);border-radius:6px}
#scores-chart text{font:10px var(--mono);fill:var(--fg2)}
#scores-chart .grid{stroke:#232b36}
#scores-chart .honest{stroke:var(--ok)}#scores-chart .assisted{stroke:var(--warn)}
#scores-chart circle.honest{fill:var(--ok)}#scores-chart circle.assisted{fill:var(--warn)}

/* map */
#map-img{max-width:100%;border:1px solid var(--line);border-radius:6px;background:#000;display:none}
#map-img.shown{display:block}
#map-err{color:var(--err);font-family:var(--mono)}

footer{display:flex;gap:14px;padding:3px 12px;border-top:1px solid var(--line);background:var(--bg1);color:var(--fg2);font:10.5px var(--mono)}
footer .r{margin-left:auto}
</style>
</head>
<body>
<header id="strip">
  <span class="brand">rimagent</span>
  <div class="stat"><label>episode</label><span id="s-episode">–</span></div>
  <div class="stat"><label>seed</label><span id="s-seed">–</span></div>
  <div class="stat phase" id="s-phase-wrap"><label>phase</label><span id="s-phase">idle</span></div>
  <div class="stat"><label>game</label><span id="s-state">–</span></div>
  <div class="stat"><label>day / hour</label><span id="s-time">–</span></div>
  <div class="stat"><label>season</label><span id="s-season">–</span></div>
  <div class="stat"><label>speed</label><span id="s-speed">–</span></div>
  <div class="stat"><label>colonists</label><span id="s-colonists">–</span></div>
  <div class="stat"><label>wealth</label><span id="s-wealth">–</span></div>
  <div class="stat"><label>mood</label><span id="s-mood">–</span></div>
  <div class="stat"><label>threat</label><span id="s-threat">–</span></div>
  <div class="stat"><label>updated</label><span id="s-updated">–</span></div>
  <div class="controls">
    <span id="agentpaused">AGENT PAUSED</span>
    <button id="b-pause" onclick="control('pause')">Pause agent</button>
    <button id="b-resume" onclick="control('resume')">Resume</button>
    <button class="primary" onclick="control('think')">Think now</button>
    <button onclick="if(confirm('End the current episode?'))control('end_episode')">End episode</button>
    <label title="Do not pause the game while the model thinks"><input type="checkbox" id="c-nopause" onchange="control('no_pause',this.checked)"> no-pause</label>
    <button class="danger" onclick="if(confirm('Kill the agent process?'))control('kill')">Kill</button>
  </div>
</header>

<nav id="tabs">
  <button data-tab="live" class="active">Live</button>
  <button data-tab="ledger">Ledger<span class="cnt" id="n-ledger"></span></button>
  <button data-tab="watchers">Watchers<span class="cnt" id="n-watchers"></span></button>
  <button data-tab="brain">Brain</button>
  <button data-tab="scores">Scores</button>
  <button data-tab="map">Map</button>
  <button data-tab="ascii">ASCII</button>
</nav>

<main>
  <section class="tab active col" id="tab-live">
    <div class="toolbar">
      <span class="dim">Think steps · newest at top · older steps collapse to one line</span>
      <button style="margin-left:auto" onclick="clearLive()">Clear</button>
    </div>
    <div class="toolbar" style="gap:6px">
      <input type="text" id="say-text" placeholder="Say something to the agent — it wakes and reads this at the start of its next step" style="flex:1;min-width:200px" onkeydown="if(event.key==='Enter')sayToAgent()">
      <button class="primary" onclick="sayToAgent()">Send</button>
      <span class="dimmer" id="say-status"></span>
    </div>
    <div class="scroll" id="live"><div class="empty">Waiting for the agent…</div></div>
  </section>

  <section class="tab col" id="tab-ledger">
    <div class="toolbar">
      <span class="dim">Game ledger</span>
      <select id="ledger-filter" onchange="applyLedgerFilter()"><option value="">all kinds</option></select>
      <label class="dim" style="margin-left:auto"><input type="checkbox" id="c-ledger-autoscroll" checked> auto-scroll</label>
    </div>
    <div class="scroll rows" id="ledger"><div class="empty">No ledger events yet.</div></div>
  </section>

  <section class="tab col" id="tab-watchers">
    <div class="toolbar">
      <span class="dim">Registered: <span id="watch-registered">…</span> — actions, alerts and errors below</span>
      <select id="watch-filter" onchange="applyWatchFilter()"><option value="">all</option><option value="action">actions</option><option value="alert">alerts</option><option value="error">errors</option></select>
    </div>
    <div class="scroll rows" id="watchers"><div class="empty">No watcher events yet.</div></div>
  </section>

  <section class="tab" id="tab-brain">
    <div class="split" style="flex:1">
      <div class="col" id="brain-left">
        <div class="toolbar"><span class="dim">brain/</span><button style="margin-left:auto" onclick="loadTree()">Refresh</button></div>
        <div class="scroll">
          <div id="brain-tree"></div>
          <div class="section-h">Changes</div>
          <div id="brain-feed" class="rows"><div class="dimmer">none yet</div></div>
        </div>
      </div>
      <div class="col" id="brain-view">
        <div class="toolbar"><span id="brain-title" class="dim">select a file</span><span class="dimmer" id="brain-meta" style="margin-left:auto"></span></div>
        <div class="scroll">
          <div id="brain-content" class="md"><div class="empty">Pick a skill, tool, watcher, notebook or journal on the left.</div></div>
          <div class="section-h" style="margin-top:20px">Git log (brain/) <button style="margin-left:8px" onclick="loadGitLog()">Refresh</button></div>
          <div id="git-log" class="dimmer">loading…</div>
          <div class="section-h" id="git-diff-h" style="display:none">Diff <span class="mono" id="git-diff-sha"></span></div>
          <pre id="git-diff" style="display:none"></pre>
        </div>
      </div>
    </div>
  </section>

  <section class="tab col" id="tab-scores">
    <div class="toolbar"><span class="dim">Episodes</span><button style="margin-left:auto" onclick="loadScores()">Refresh</button></div>
    <div class="scroll">
      <svg id="scores-chart" viewBox="0 0 900 260" preserveAspectRatio="xMidYMid meet"></svg>
      <div style="height:10px"></div>
      <div id="scores-table"><div class="empty">No episodes scored yet.</div></div>
    </div>
  </section>

  <section class="tab col" id="tab-map">
    <div class="toolbar">
      <label class="dim">x <input type="number" id="m-x" placeholder="home"></label>
      <label class="dim">z <input type="number" id="m-z" placeholder="home"></label>
      <label class="dim">w <input type="number" id="m-w" value="80" min="10" max="300"></label>
      <button class="primary" onclick="loadMap()">Refresh</button>
      <button onclick="mx.value='';mz.value='';loadMap()">Home</button>
      <label class="dim"><input type="checkbox" id="c-map-auto"> auto every 30s</label>
      <span id="map-err"></span>
      <span class="dimmer" id="map-when" style="margin-left:auto"></span>
    </div>
    <div class="scroll"><img id="map-img" alt="map screenshot"></div>
  </section>

  <section class="tab col" id="tab-ascii">
    <div class="toolbar">
      <label class="dim">x <input type="number" id="a-x" placeholder="home"></label>
      <label class="dim">z <input type="number" id="a-z" placeholder="home"></label>
      <label class="dim">w <input type="number" id="a-w" value="80" min="10" max="150"></label>
      <label class="dim">h <input type="number" id="a-h" value="50" min="10" max="150"></label>
      <label class="dim">layer <select id="a-layer"><option>all</option><option>terrain</option><option>buildings</option><option>zones</option><option>pawns</option><option>items</option><option>roof</option><option>fog</option><option>home</option></select></label>
      <button class="primary" onclick="loadAscii()">Refresh</button>
      <button onclick="$('a-x').value='';$('a-z').value='';loadAscii()">Home</button>
      <button onclick="loadOverview()">Whole map</button>
      <label class="dim"><input type="checkbox" id="c-ascii-auto"> auto every 15s</label>
      <span id="ascii-err"></span>
      <span class="dimmer" id="ascii-when" style="margin-left:auto"></span>
    </div>
    <div class="dimmer" id="ascii-legend" style="padding:4px 8px;white-space:pre-wrap"></div>
    <div class="scroll"><pre id="ascii-grid" style="font-family:ui-monospace,Menlo,monospace;font-size:11px;line-height:11px;letter-spacing:1px;margin:0;padding:8px"></pre></div>
  </section>
</main>

<footer>
  <span><span id="conn"></span><span id="conn-txt">connecting</span></span>
  <span>seq <span id="f-seq">0</span></span>
  <span>events <span id="f-n">0</span></span>
  <span id="f-msg"></span>
  <span class="r" id="f-ctl"></span>
</footer>

<script>
'use strict';
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmtT = (t) => { const d = new Date(t * 1000); return d.toLocaleTimeString([], {hour12:false}); };
const fmtN = (v) => (v == null || v === '') ? '–' : (typeof v === 'number' ? (Math.abs(v) >= 1000 ? Math.round(v).toLocaleString() : (Number.isInteger(v) ? v : v.toFixed(1))) : String(v));
const pick = (o, ...keys) => { for (const k of keys) if (o && o[k] != null) return o[k]; return null; };
const tabsEl = $('tabs');
let nEvents = 0, lastSeq = 0, es = null, esRetry = null;
let state = {};

// ---------- tabs ----------
tabsEl.addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b) return;
  document.querySelectorAll('#tabs button').forEach(x => x.classList.toggle('active', x === b));
  document.querySelectorAll('main .tab').forEach(x => x.classList.toggle('active', x.id === 'tab-' + b.dataset.tab));
  try { localStorage.setItem('rimagent.tab', b.dataset.tab); } catch (_) {}
  if (b.dataset.tab === 'brain' && !treeLoaded) { loadTree(); loadGitLog(); }
  if (b.dataset.tab === 'watchers') loadTree();
  if (b.dataset.tab === 'scores' && !scoresLoaded) loadScores();
  if (b.dataset.tab === 'map' && !mapLoaded) loadMap();
  if (b.dataset.tab === 'live') scrollBottom($('live'), true);
  if (b.dataset.tab === 'ledger') scrollBottom($('ledger'), true);
});
try { const t = localStorage.getItem('rimagent.tab'); if (t) document.querySelector(`#tabs button[data-tab="${t}"]`)?.click(); } catch (_) {}

function nearBottom(el) { return el.scrollHeight - el.scrollTop - el.clientHeight < 60; }
function scrollBottom(el, force) { if (force || nearBottom(el)) el.scrollTop = el.scrollHeight; }

// ---------- status strip ----------
function renderStatus() {
  const s = state;
  $('s-episode').textContent = fmtN(s.episode);
  $('s-seed').textContent = s.seed ?? '–';
  const ph = s.phase || 'idle';
  $('s-phase').textContent = ph;
  $('s-phase-wrap').className = 'stat phase ' + ph;
  $('s-state').textContent = s.state ?? '–';
  const day = pick(s, 'day'), hour = pick(s, 'hour');
  $('s-time').textContent = day == null ? '–' : `d${day} ${hour == null ? '' : String(hour).padStart(2, '0') + 'h'}`;
  $('s-season').textContent = s.season ?? '–';
  $('s-speed').textContent = s.speed == null ? '–' : (s.paused ? `⏸ ${s.speed}` : `▶ ${s.speed}`);
  $('s-colonists').textContent = fmtN(pick(s, 'colonists', 'colonist_count'));
  $('s-wealth').textContent = fmtN(pick(s, 'wealth', 'wealth_total'));
  const mood = pick(s, 'mood_avg', 'mood');
  $('s-mood').textContent = mood == null ? '–' : fmtN(mood) + '%';
  $('s-threat').textContent = fmtN(pick(s, 'threat_points', 'threat'));
  $('s-updated').textContent = s._t ? fmtT(s._t) : '–';
}
function setControls(c) {
  if (!c) return;
  $('agentpaused').classList.toggle('on', !!c.paused);
  $('b-pause').disabled = !!c.paused; $('b-resume').disabled = !c.paused;
  if (c.no_pause != null) $('c-nopause').checked = !!c.no_pause;
}
async function control(action, value) {
  try {
    const r = await fetch('/api/control', {method: 'POST', headers: {'content-type': 'application/json'}, body: JSON.stringify({action, value})});
    const j = await r.json();
    $('f-ctl').textContent = j.ok ? `${action} ok` : `${action}: ${j.error || j.detail || 'failed'}`;
    setControls(j);
  } catch (e) { $('f-ctl').textContent = `${action}: ${e}`; }
}
async function pollState() {
  try {
    const j = await (await fetch('/api/state')).json();
    Object.assign(state, j.bus || {}, j.game || {});
    state._t = Date.now() / 1000;
    renderStatus(); setControls(j.controls);
  } catch (_) {}
}

// ---------- live transcript ----------
const live = $('live');
const KEEP_OPEN = 5, KEEP_STEPS = 60;
let curStep = null, stepCount = 0;
function clearLive() { live.innerHTML = ''; curStep = null; }
function liveAppend(node) {
  const e = live.querySelector('.empty'); if (e) e.remove();
  if (curStep) curStep.querySelector('.step-body').appendChild(node); else live.insertBefore(node, live.firstChild);
}
async function sayToAgent() {
  const inp = $('say-text'); const text = inp.value.trim(); if (!text) return;
  $('say-status').textContent = 'sending…';
  try {
    const r = await fetch('/api/say', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text }) });
    const j = await r.json(); $('say-status').textContent = j.ok ? 'queued — the agent will read it next step' : ('failed: ' + (j.error || '')); if (j.ok) inp.value = '';
  } catch (e) { $('say-status').textContent = 'failed: ' + e; }
  setTimeout(() => { $('say-status').textContent = ''; }, 6000);
}
function newStep(d, t) {
  const s = document.createElement('div');
  s.className = 'step'; stepCount++;
  s.innerHTML = `<div class="step-head"><span class="dim">${fmtT(t)}</span><span>step ${esc(d.step ?? stepCount)}</span><span class="trig">${esc(d.trigger || '')}</span><span class="sum"></span></div><div class="step-body"></div>`;
  s.querySelector('.step-head').onclick = () => s.classList.toggle('collapsed');
  s._calls = 0; s._tools = [];
  const e = live.querySelector('.empty'); if (e) e.remove();
  live.insertBefore(s, live.firstChild); curStep = s;
  const steps = live.querySelectorAll('.step');
  steps.forEach((x, i) => { if (i >= KEEP_OPEN) x.classList.add('collapsed'); });
  const all = live.querySelectorAll('.step'); if (all.length > KEEP_STEPS) all[all.length - 1].remove();
}
function ensureStep(t) { if (!curStep) newStep({trigger: '(in progress)'}, t); }
function updateSummary(s) {
  const sum = s.querySelector('.sum');
  sum.textContent = `${s._calls} call${s._calls === 1 ? '' : 's'}` + (s._tools.length ? ' · ' + s._tools.slice(0, 6).join(', ') + (s._tools.length > 6 ? '…' : '') : '') + (s._elapsed ? ` · ${s._elapsed.toFixed(1)}s` : '');
}
function itemReasoning(d) {
  const n = document.createElement('details'); n.className = 'item reasoning';
  n.innerHTML = `<summary>thinking… <span class="dimmer">(${(d.text || '').length} chars)</span></summary><pre>${esc(d.text)}</pre>`;
  return n;
}
function itemAssistant(d) { const n = document.createElement('div'); n.className = 'item assistant'; n.textContent = d.text || ''; return n; }
function itemToolCall(d) {
  const n = document.createElement('div'); n.className = 'item tool pending'; n.dataset.id = d.id || '';
  const args = JSON.stringify(d.args ?? {});
  n.innerHTML = `<div class="tool-head"><span class="tname">${esc(d.name)}</span><span class="argprev">${esc(args)}</span><span class="badge">…</span><span class="el"></span></div><pre class="args" hidden>${esc(JSON.stringify(d.args ?? {}, null, 2))}</pre>`;
  n.querySelector('.tool-head').onclick = () => { const p = n.querySelector('pre.args'); p.hidden = !p.hidden; };
  if (curStep) { curStep._calls++; curStep._tools.push(d.name); updateSummary(curStep); }
  return n;
}
function attachResult(d) {
  let card = null;
  if (d.id && curStep) card = curStep.querySelector(`.tool[data-id="${CSS.escape(d.id)}"]`);
  if (!card && curStep) { const c = curStep.querySelectorAll('.tool.pending'); card = c[c.length - 1] || null; }
  const txt = d.text || '';
  const LIM = 500;
  const res = document.createElement('div'); res.className = 'result';
  const pre = document.createElement('pre');
  const long = txt.length > LIM;
  pre.textContent = long ? txt.slice(0, LIM) + '…' : txt;
  res.appendChild(pre);
  if (long) { const m = document.createElement('span'); m.className = 'more'; m.textContent = `show all (${txt.length} chars)`; m.onclick = () => { pre.textContent = txt; m.remove(); }; res.appendChild(m); }
  if (card) {
    card.classList.remove('pending');
    const b = card.querySelector('.badge'); b.textContent = d.ok ? 'ok' : 'err'; b.className = 'badge ' + (d.ok ? 'ok' : 'err');
    card.querySelector('.el').textContent = d.elapsed != null ? `${Number(d.elapsed).toFixed(2)}s` : '';
    card.appendChild(res);

  } else {
    const n = document.createElement('div'); n.className = 'item tool';
    n.innerHTML = `<div class="tool-head"><span class="tname">${esc(d.name)}</span><span class="badge ${d.ok ? 'ok' : 'err'}">${d.ok ? 'ok' : 'err'}</span><span class="el">${d.elapsed != null ? Number(d.elapsed).toFixed(2) + 's' : ''}</span></div>`;
    n.appendChild(res); liveAppend(n);
  }
}
function itemThinkEnd(d) {
  const n = document.createElement('div'); n.className = 'item think-end';
  const wake = d.wake ? JSON.stringify(d.wake) : '';
  n.innerHTML = `<div><span class="badge pur">done</span> <span class="dim">${d.calls ?? 0} calls · ${d.elapsed != null ? Number(d.elapsed).toFixed(1) + 's' : ''}</span></div>` +
    (d.notes ? `<div style="white-space:pre-wrap;margin-top:3px">${esc(d.notes)}</div>` : '') +
    (wake ? `<div class="wake">wake: ${esc(wake)}</div>` : '');
  if (curStep) { curStep._elapsed = d.elapsed; updateSummary(curStep); }
  return n;
}
function sysLine(cls, t, text) { const n = document.createElement('div'); n.className = 'sys ' + cls; n.textContent = `${fmtT(t)}  ${text}`; return n; }

// ---------- ledger ----------
const ledger = $('ledger'), ledgerKinds = new Set();
let nLedger = 0;
const KIND_CLS = {letter: 'info', incident: 'warn', colonist_died: 'err', pawn_died: 'err', colonist_downed: 'err', mental_break: 'warn', hostile_group: 'err', quest: 'pur', building_lost: 'warn', message: '', game: 'teal', research: 'teal'};
function addLedger(d, t) {
  const e = ledger.querySelector('.empty'); if (e) e.remove();
  const k = d.kind || '?';
  if (!ledgerKinds.has(k)) { ledgerKinds.add(k); const o = document.createElement('option'); o.value = k; o.textContent = k; $('ledger-filter').appendChild(o); }
  const r = document.createElement('div'); r.className = 'row'; r.dataset.kind = k;
  const f = $('ledger-filter').value; if (f && f !== k) r.classList.add('hidden');
  const when = d.day != null ? `d${d.day} ${d.hour != null ? String(d.hour).padStart(2, '0') + 'h' : ''}` : fmtT(t);
  const extras = Object.entries(d).filter(([kk]) => !['kind', 'text', 'tick', 'day', 'hour', 'seq', 'id'].includes(kk)).map(([kk, v]) => `${kk}=${typeof v === 'object' ? JSON.stringify(v) : v}`).join(' ');
  r.innerHTML = `<span class="badge ${KIND_CLS[k] ?? ''}">${esc(k)}</span><span class="when">${esc(when)}</span><span class="text">${esc(d.text || '')}${extras ? ` <span class="extra">${esc(extras)}</span>` : ''}</span>`;
  ledger.appendChild(r); nLedger++; $('n-ledger').textContent = nLedger;
  while (ledger.children.length > 2000) ledger.firstChild.remove();
  if ($('c-ledger-autoscroll').checked) scrollBottom(ledger);
}
function applyLedgerFilter() { const f = $('ledger-filter').value; ledger.querySelectorAll('.row').forEach(r => r.classList.toggle('hidden', !!f && r.dataset.kind !== f)); scrollBottom(ledger, true); }

// ---------- watchers ----------
const watchers = $('watchers'); let nWatch = 0;
function addWatcher(d, t) {
  const e = watchers.querySelector('.empty'); if (e) e.remove();
  const typ = d.error != null ? 'error' : d.alert != null ? 'alert' : d.action != null ? 'action' : 'event';
  const val = d[typ] ?? d;
  const r = document.createElement('div'); r.className = 'row'; r.dataset.type = typ;
  const f = $('watch-filter').value; if (f && f !== typ) r.classList.add('hidden');
  r.innerHTML = `<span class="when">${fmtT(t)}</span><span class="badge ${typ === 'error' ? 'err' : typ === 'alert' ? 'warn' : 'teal'}">${typ}</span><span class="mono" style="color:var(--pur)">${esc(d.name || '?')}</span><span class="text">${esc(typeof val === 'string' ? val : JSON.stringify(val))}</span>`;
  watchers.appendChild(r); nWatch++; $('n-watchers').textContent = nWatch;
  while (watchers.children.length > 1000) watchers.firstChild.remove();
  scrollBottom(watchers);
}
function applyWatchFilter() { const f = $('watch-filter').value; watchers.querySelectorAll('.row').forEach(r => r.classList.toggle('hidden', !!f && r.dataset.type !== f)); }

// ---------- brain ----------
let treeLoaded = false, treeTimer = null, curFile = null;
async function loadTree() {
  try {
    const j = await (await fetch('/api/brain/tree')).json();
    treeLoaded = true;
    const tree = $('brain-tree'); tree.innerHTML = '';
    const grp = (title, items) => { const h = document.createElement('h4'); h.textContent = title; tree.appendChild(h); if (!items.length) { const d = document.createElement('div'); d.className = 'dimmer'; d.style.padding = '0 6px'; d.textContent = '(none)'; tree.appendChild(d); } items.forEach(it => tree.appendChild(it)); };
    const node = (kind, name, label, chars, desc, extra) => {
      const n = document.createElement('div'); n.className = 'node'; n.dataset.key = kind + ':' + name;
      n.innerHTML = `<span class="n">${esc(label)}${extra ? ` <span class="badge info" style="font-size:9px">${extra}</span>` : ''}${desc ? `<span class="d">${esc(desc)}</span>` : ''}</span><span class="c">${chars != null ? chars + 'c' : ''}</span>`;
      n.onclick = () => openFile(kind, name, label);
      if (curFile === n.dataset.key) n.classList.add('sel');
      return n;
    };
    grp(`skills (${j.skills.length})`, j.skills.map(s => node('skill', s.file || s.name, s.name, s.chars, s.description, s.always ? 'always' : '')));
    grp(`tools (${j.tools.length})`, j.tools.map(t => node('tool', t, t)));
    grp(`watchers (${j.watchers.length})`, j.watchers.map(w => node('watcher', w, w)));
    const wr = $('watch-registered'); if (wr) wr.innerHTML = j.watchers.length ? j.watchers.map(w => `<a href="#" onclick="document.querySelector('#tabs button[data-tab=brain]').click();openFile('watcher','${esc(w)}','${esc(w)}');return false">${esc(w)}</a>`).join(', ') : '(none yet)';
    grp('memory', [node('notebook', '', 'notebook.md', j.notebook_chars), node('journal', '', 'journal.md', j.journal_chars)]);
  } catch (e) { $('brain-tree').innerHTML = `<div class="dimmer">tree error: ${esc(e)}</div>`; }
}
async function openFile(kind, name, label) {
  curFile = kind + ':' + name;
  document.querySelectorAll('#brain-tree .node').forEach(n => n.classList.toggle('sel', n.dataset.key === curFile));
  $('brain-title').textContent = `${kind}/${label}`; $('brain-meta').textContent = '';
  try {
    const r = await fetch(`/api/brain/file?kind=${encodeURIComponent(kind)}&name=${encodeURIComponent(name)}`);
    const j = await r.json();
    if (!r.ok) { $('brain-content').innerHTML = `<div class="empty">${esc(j.detail || r.status)}</div>`; return; }
    $('brain-meta').textContent = `${j.text.length} chars`;
    const isMd = /\.md$/i.test(j.name) || kind === 'notebook' || kind === 'journal';
    $('brain-content').innerHTML = isMd ? renderMd(j.text) : `<pre>${esc(j.text)}</pre>`;
  } catch (e) { $('brain-content').innerHTML = `<div class="empty">${esc(e)}</div>`; }
}
function inline(s) {
  return esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>').replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, '$1<i>$2</i>');
}
function renderMd(text) {
  const out = []; let list = null, para = [], code = null;
  const flushP = () => { if (para.length) { out.push('<p>' + para.map(inline).join('<br>') + '</p>'); para = []; } };
  const flushL = () => { if (list) { out.push(`</${list}>`); list = null; } };
  for (const raw of text.split('\n')) {
    if (code !== null) { if (/^```/.test(raw)) { out.push(`<pre>${esc(code.join('\n'))}</pre>`); code = null; } else code.push(raw); continue; }
    const line = raw.replace(/\s+$/, '');
    let m;
    if (/^```/.test(line)) { flushP(); flushL(); code = []; continue; }
    if (/^---+$/.test(line)) { flushP(); flushL(); out.push('<hr>'); continue; }
    if ((m = line.match(/^(#{1,4})\s+(.*)$/))) { flushP(); flushL(); out.push(`<h${m[1].length}>${inline(m[2])}</h${m[1].length}>`); continue; }
    if ((m = line.match(/^\s*[-*+]\s+(.*)$/))) { flushP(); if (list !== 'ul') { flushL(); out.push('<ul>'); list = 'ul'; } out.push(`<li>${inline(m[1])}</li>`); continue; }
    if ((m = line.match(/^\s*\d+[.)]\s+(.*)$/))) { flushP(); if (list !== 'ol') { flushL(); out.push('<ol>'); list = 'ol'; } out.push(`<li>${inline(m[1])}</li>`); continue; }
    if (line.trim() === '') { flushP(); flushL(); continue; }
    if (list && /^\s{2,}/.test(raw)) { out[out.length - 1] = out[out.length - 1].replace(/<\/li>$/, '<br>' + inline(line.trim()) + '</li>'); continue; }
    flushL(); para.push(line);
  }
  if (code !== null) out.push(`<pre>${esc(code.join('\n'))}</pre>`);
  flushP(); flushL();
  return out.join('\n');
}
async function loadGitLog() {
  try {
    const t = await (await fetch('/api/git/log')).text();
    const el = $('git-log'); el.innerHTML = '';
    const lines = t.split('\n').filter(Boolean);
    if (!lines.length || lines[0].startsWith('(no history')) { el.textContent = t || '(no commits)'; return; }
    for (const l of lines) {
      const sha = l.split(' ')[0];
      const d = document.createElement('div'); d.className = 'line'; d.dataset.sha = sha;
      d.innerHTML = `<span class="sha">${esc(sha)}</span> ${esc(l.slice(sha.length + 1))}`;
      d.onclick = () => showDiff(sha);
      el.appendChild(d);
    }
  } catch (e) { $('git-log').textContent = String(e); }
}
async function showDiff(sha) {
  document.querySelectorAll('#git-log .line').forEach(l => l.classList.toggle('sel', l.dataset.sha === sha));
  $('git-diff-h').style.display = ''; $('git-diff-sha').textContent = sha;
  const pre = $('git-diff'); pre.style.display = ''; pre.textContent = 'loading…';
  try {
    const t = await (await fetch(`/api/git/diff?sha=${encodeURIComponent(sha)}`)).text();
    pre.innerHTML = t.split('\n').map(l => {
      const c = l.startsWith('+++') || l.startsWith('---') ? 'meta' : l.startsWith('+') ? 'add' : l.startsWith('-') ? 'del' : l.startsWith('@@') ? 'hunk' : /^(diff|index|commit|Author|Date)/.test(l) ? 'meta' : '';
      return c ? `<span class="${c}">${esc(l)}</span>` : esc(l);
    }).join('\n');
    pre.scrollIntoView({block: 'nearest'});
  } catch (e) { pre.textContent = String(e); }
}
function addBrainChange(d, t) {
  const el = $('brain-feed'); const dm = el.querySelector('.dimmer'); if (dm) dm.remove();
  const r = document.createElement('div'); r.className = 'row';
  r.innerHTML = `<span class="when">${fmtT(t)}</span><span class="badge ${d.kind === 'git' ? 'warn' : 'pur'}" style="min-width:60px">${esc(d.kind)}</span><span class="text"><span class="mono">${esc(d.action || '')}</span> ${esc(d.name || '')}${d.sha ? ` <span class="dimmer mono">${esc(String(d.sha).slice(0, 7))}</span>` : ''}</span>`;
  el.appendChild(r); while (el.children.length > 200) el.firstChild.remove();
  clearTimeout(treeTimer); treeTimer = setTimeout(() => { loadTree(); if (d.kind === 'git') loadGitLog(); }, 800);
}

// ---------- scores ----------
let scoresLoaded = false;
async function loadScores() {
  try {
    const rows = await (await fetch('/api/scores')).json();
    scoresLoaded = true;
    drawChart(rows);
    const tb = $('scores-table');
    if (!rows.length) { tb.innerHTML = '<div class="empty">No episodes scored yet.</div>'; return; }
    const cols = ['episode', 'seed', 'days', 'colonists', 'deaths', 'wealth', 'score', 'assisted', 'brain_sha', 'ended', 't'];
    tb.innerHTML = `<table><thead><tr>${cols.map(c => `<th>${c}</th>`).join('')}</tr></thead><tbody>` +
      rows.slice().reverse().map(r => `<tr>${cols.map(c => { let v = r[c]; if (c === 't' && v) v = new Date(v * 1000).toLocaleString(); if (c === 'brain_sha' && v) v = String(v).slice(0, 7); if (c === 'assisted') v = v ? 'yes' : 'no'; if (c === 'wealth' || c === 'score') v = fmtN(v); return `<td>${esc(v ?? '')}</td>`; }).join('')}</tr>`).join('') + '</tbody></table>';
  } catch (e) { $('scores-table').innerHTML = `<div class="empty">${esc(e)}</div>`; }
}
function drawChart(rows) {
  const svg = $('scores-chart'); const W = 900, H = 260, L = 50, R = 20, T = 18, B = 30;
  const pts = rows.filter(r => r.score != null).map((r, i) => ({x: Number(r.episode ?? i + 1), y: Number(r.score), a: !!r.assisted}));
  if (!pts.length) { svg.innerHTML = `<text x="${W / 2}" y="${H / 2}" text-anchor="middle">no scores yet</text>`; return; }
  const xs = pts.map(p => p.x), ys = pts.map(p => p.y);
  let x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(0, ...ys), y1 = Math.max(...ys);
  if (x1 === x0) { x0 -= 1; x1 += 1; } if (y1 === y0) y1 = y0 + 1;
  const raw = (y1 - y0) / 5, mag = Math.pow(10, Math.floor(Math.log10(raw))), step = [1, 2, 2.5, 5, 10].map(m => m * mag).find(v => v >= raw);
  y0 = Math.floor(y0 / step) * step; y1 = Math.ceil((y1 + step * 0.3) / step) * step;
  const sx = x => L + (x - x0) / (x1 - x0) * (W - L - R), sy = y => T + (y1 - y) / (y1 - y0) * (H - T - B);
  let g = '';
  for (let y = y0; y <= y1 + 1e-9; y += step) { g += `<line class="grid" x1="${L}" x2="${W - R}" y1="${sy(y)}" y2="${sy(y)}"/><text x="${L - 6}" y="${sy(y) + 3}" text-anchor="end">${Math.round(y)}</text>`; }
  const xt = Math.min(12, x1 - x0); for (let i = 0; i <= xt; i++) { const x = Math.round(x0 + (x1 - x0) * i / xt); g += `<text x="${sx(x)}" y="${H - B + 14}" text-anchor="middle">${x}</text>`; }
  g += `<text x="${W - R}" y="${H - 4}" text-anchor="end">episode</text><text x="${L}" y="10">score</text>`;
  for (const [cls, sel] of [['honest', p => !p.a], ['assisted', p => p.a]]) {
    const s = pts.filter(sel).sort((a, b) => a.x - b.x); if (!s.length) continue;
    g += `<polyline class="${cls}" fill="none" stroke-width="1.8" points="${s.map(p => `${sx(p.x)},${sy(p.y)}`).join(' ')}"/>`;
    g += s.map(p => `<circle class="${cls}" cx="${sx(p.x)}" cy="${sy(p.y)}" r="3"><title>ep ${p.x}: ${p.y}${p.a ? ' (assisted)' : ''}</title></circle>`).join('');
  }
  g += `<rect x="${W - R - 150}" y="${T}" width="10" height="3" fill="var(--ok)"/><text x="${W - R - 135}" y="${T + 4}">honest</text><rect x="${W - R - 80}" y="${T}" width="10" height="3" fill="var(--warn)"/><text x="${W - R - 65}" y="${T + 4}">assisted</text>`;
  svg.innerHTML = g;
}

// ---------- map ----------
let mapLoaded = false; const mx = $('m-x'), mz = $('m-z');
function loadMap() {
  mapLoaded = true;
  const q = new URLSearchParams(); if (mx.value !== '') q.set('x', mx.value); if (mz.value !== '') q.set('z', mz.value); q.set('w', $('m-w').value || 80); q.set('t', Date.now());
  const img = $('map-img'); $('map-err').textContent = ''; img.style.opacity = .5;
  img.onload = () => { img.classList.add('shown'); img.style.opacity = 1; $('map-when').textContent = 'captured ' + new Date().toLocaleTimeString(); };
  img.onerror = async () => { img.classList.remove('shown'); img.style.opacity = 1; try { const j = await (await fetch('/screenshot.png?' + q)).json(); $('map-err').textContent = j.error || 'screenshot failed'; } catch (_) { $('map-err').textContent = 'screenshot failed'; } };
  img.src = '/screenshot.png?' + q;
}
setInterval(() => { if ($('c-map-auto').checked && $('tab-map').classList.contains('active')) loadMap(); }, 30000);

// ---------- ascii ----------
const ASCII_COLORS = { '@': '#5fd7ff', '!': '#ff5f5f', 'a': '#87d787', 'w': '#afaf5f', 'n': '#d7afff', '#': '#c0c0c0', '+': '#ffd75f', '^': '#8a8a8a', 'o': '#ffaf00', 'b': '#ff87d7', 't': '#d7d787', 's': '#ff8700', 'r': '#87afff', 'g': '#ffff5f', '%': '#ff5faf', 'x': '#d0d0d0', 'p': '#5fafff', 'S': '#5f87ff', 'G': '#5fff5f', '~': '#0087ff', 'T': '#00af00', ',': '#5f875f', 'i': '#ffffaf', '*': '#ff0000', 'f': '#875f00', '?': '#303030', 'H': '#5f87ff', 'B': '#d0d0d0' };
function paintAscii(grid) {
  const esc = t => t.replace(/&/g, '&amp;').replace(/</g, '&lt;');
  return grid.split('\n').map((line, i) => {
    if (i < 2) return `<span style="color:#666">${esc(line)}</span>`;
    const head = line.slice(0, 5), body = line.slice(5);
    let out = `<span style="color:#666">${esc(head)}</span>`;
    for (const ch of body) { const c = ASCII_COLORS[ch]; out += c ? `<span style="color:${c}">${esc(ch)}</span>` : esc(ch); }
    return out;
  }).join('\n');
}
async function loadAscii() {
  const q = new URLSearchParams(); const ax = $('a-x').value, az = $('a-z').value;
  if (ax !== '') q.set('x', ax); if (az !== '') q.set('z', az); q.set('w', $('a-w').value || 80); q.set('h', $('a-h').value || 50); q.set('layer', $('a-layer').value);
  $('ascii-err').textContent = '';
  try {
    const r = await fetch('/api/ascii?' + q); const j = await r.json();
    if (j.error) { $('ascii-err').textContent = j.error; return; }
    $('ascii-legend').textContent = j.legend || ''; $('ascii-grid').innerHTML = paintAscii(j.grid || '');
    $('ascii-when').textContent = `box ${JSON.stringify(j.box && j.box.min)}..${JSON.stringify(j.box && j.box.max)} · ${new Date().toLocaleTimeString()}`;
  } catch (e) { $('ascii-err').textContent = 'failed: ' + e; }
}
async function loadOverview() {
  $('ascii-err').textContent = '';
  try {
    const j = await (await fetch('/api/overview?blocks=100')).json();
    if (j.error) { $('ascii-err').textContent = j.error; return; }
    $('ascii-legend').textContent = (j.legend || '') + `  (block size ${j.block_size})`; $('ascii-grid').innerHTML = paintAscii(j.grid || '');
    $('ascii-when').textContent = 'whole map · ' + new Date().toLocaleTimeString();
  } catch (e) { $('ascii-err').textContent = 'failed: ' + e; }
}
setInterval(() => { if ($('c-ascii-auto').checked && $('tab-ascii').classList.contains('active')) loadAscii(); }, 15000);
document.querySelector('#tabs button[data-tab="ascii"]').addEventListener('click', () => { if (!$('ascii-grid').innerHTML) loadAscii(); });

// ---------- event dispatch ----------
function handle(ev) {
  const d = ev.data || {}, t = ev.t;
  nEvents++; lastSeq = Math.max(lastSeq, ev.seq);
  switch (ev.kind) {
    case 'status': Object.assign(state, d); state._t = t; renderStatus(); break;
    case 'think_start': newStep(d, t); break;
    case 'reasoning': ensureStep(t); liveAppend(itemReasoning(d)); break;
    case 'assistant': ensureStep(t); liveAppend(itemAssistant(d)); break;
    case 'tool_call': ensureStep(t); liveAppend(itemToolCall(d)); break;
    case 'tool_result': ensureStep(t); attachResult(d); break;
    case 'think_end': ensureStep(t); liveAppend(itemThinkEnd(d)); curStep = null; break;
    case 'ledger': addLedger(d, t); break;
    case 'watcher': addWatcher(d, t); break;
    case 'brain_change': addBrainChange(d, t); break;
    case 'episode_start': { const s = curStep; curStep = null; liveAppend(sysLine('episode', t, `episode ${d.episode} started · seed ${d.seed}`)); curStep = s; state.episode = d.episode; state.seed = d.seed; renderStatus(); break; }
    case 'episode_end': { const s = curStep; curStep = null; liveAppend(sysLine('episode', t, `episode ${d.episode} ended · score ${d.score}${d.assisted ? ' (assisted)' : ''} · ${d.reason || ''}`)); curStep = s; if (scoresLoaded) loadScores(); break; }
    case 'error': { const s = curStep; curStep = null; liveAppend(sysLine('error', t, 'error: ' + (d.text || JSON.stringify(d)))); curStep = s; break; }
    case 'log': { const s = curStep; curStep = null; liveAppend(sysLine('log', t, d.text || JSON.stringify(d))); curStep = s; break; }
    case 'operator': { const s = curStep; curStep = null; const n = sysLine('log', t, '🧑 you: ' + (d.text || '')); n.style.borderLeft = '3px solid var(--warn)'; liveAppend(n); curStep = s; break; }
    default: break;
  }
  $('f-seq').textContent = lastSeq; $('f-n').textContent = nEvents;
}
function setConn(on, txt) { $('conn').classList.toggle('on', on); $('conn-txt').textContent = txt; }

async function hydrate() {
  await pollState();
  try {
    const evs = await (await fetch('/api/events?since=0&limit=2000')).json();
    for (const e of evs) handle(e);
  } catch (e) { $('f-msg').textContent = 'hydrate failed: ' + e; }
  connect();
}
function connect() {
  if (es) { es.close(); es = null; }
  setConn(false, 'connecting');
  es = new EventSource(`/stream?since=${lastSeq}`);
  es.onopen = () => setConn(true, 'live');
  es.onmessage = m => { try { handle(JSON.parse(m.data)); } catch (e) { console.error(e); } };
  es.onerror = () => { setConn(false, 'reconnecting'); es.close(); es = null; clearTimeout(esRetry); esRetry = setTimeout(connect, 2000); };
}
hydrate();
setInterval(pollState, 10000);
</script>
</body>
</html>
"""


# --------------------------------------------------------------------------------------
# Standalone demo: `python -m rimagent.dashboard.app`
# --------------------------------------------------------------------------------------

def _demo() -> None:  # pragma: no cover - manual eyeballing
    import random

    from ..bus import Bus
    from ..config import CONFIG

    bus = Bus(log_file=False)

    class FakeBridge:
        def status(self):
            return {"state": "playing", "day": self.day, "hour": int(time.time() / 2) % 24, "season": "Spring", "speed": 3, "paused": False, "colonists": 3, "seq": 0}

        day = 3

        def events(self, since: int, limit: int = 500):
            return {"events": [], "next": since}

        def screenshot(self, x=None, z=None, w=60, **_):
            raise RuntimeError("demo: no game attached")

    class Controls:
        paused = False
        no_pause = False

        def pause(self):
            self.paused = True
            bus.emit("log", {"text": "agent paused"})

        def resume(self):
            self.paused = False
            bus.emit("log", {"text": "agent resumed"})

        def think_now(self):
            bus.emit("log", {"text": "think requested"})

        def end_episode(self):
            bus.emit("episode_end", {"episode": 1, "score": 123.4, "reason": "manual", "assisted": False, "brain_sha": "deadbeef"})

        def set_no_pause(self, v: bool):
            self.no_pause = v

        def kill(self):
            bus.emit("error", {"text": "kill requested (demo ignores it)"})

    def producer() -> None:
        bus.emit("episode_start", {"episode": 1, "seed": "rimagent-1"})
        step = 0
        while True:
            step += 1
            day = 3 + step // 4
            bus.emit("status", {"state": "playing", "episode": 1, "seed": "rimagent-1", "phase": "thinking", "day": day, "hour": (step * 6) % 24, "season": "Spring", "speed": 0, "paused": True, "colonists": 3, "wealth": 15000 + step * 340, "mood_avg": 60 + random.randint(-10, 10), "threat_points": 120 + step * 9})
            bus.emit("think_start", {"trigger": random.choice(["scheduled wake (6h)", "letter: Raid", "colonist_downed"]), "step": step})
            time.sleep(1)
            bus.emit("reasoning", {"text": "Let me look at the colony first.\n" + "The colonists need food and a wall on the north side. " * random.randint(3, 30)})
            time.sleep(1)
            bus.emit("assistant", {"text": "Checking colony state and then queuing a growing zone."})
            for name, args in (("state.colony", {}), ("zones.create", {"kind": "growing", "rect": [100, 100, 108, 106], "plant": "Rice"}), ("skills.read", {"name": "early-food"})):
                cid = f"call-{step}-{name}"
                bus.emit("tool_call", {"name": name, "args": args, "id": cid})
                time.sleep(1)
                ok = random.random() > 0.15
                bus.emit("tool_result", {"name": name, "id": cid, "ok": ok, "elapsed": random.random() * 2, "text": ("{\"colonists\": 3, \"food\": 42, \"wealth\": 15230}\n" * (1 if ok else 0) + ("error: no such skill" if not ok else "")) * random.randint(1, 12)})
            bus.emit("ledger", {"kind": random.choice(["letter", "message", "incident", "colonist_downed", "quest"]), "text": random.choice(["Raid from pirates", "Rice harvested", "Wanderer joins", "Bob is downed"]), "tick": step * 2500, "day": day, "hour": (step * 6) % 24})
            if step % 3 == 0:
                bus.emit("watcher", {"name": "food-guard", "action": "queued hunting job for muffalo"})
            if step % 5 == 0:
                bus.emit("watcher", {"name": "threat-scan", "alert": {"danger": "High", "hostiles": 4}})
            if step % 7 == 0:
                bus.emit("brain_change", {"kind": "skill", "name": "early-food", "action": "write"})
            time.sleep(1)
            bus.emit("think_end", {"notes": f"Step {step}: queued growing zone; watch the north wall.", "wake": {"hours": 6, "on": ["letter", "incident"]}, "calls": 3, "elapsed": 4.2})
            bus.emit("status", {"phase": "playing", "speed": 3, "paused": False})
            time.sleep(2)

    threading.Thread(target=producer, daemon=True).start()
    import uvicorn

    port = int(CONFIG["dashboard"]["port"])
    print(f"demo dashboard: http://localhost:{port}")
    uvicorn.run(create_app(bus, FakeBridge(), Controls()), host="127.0.0.1", port=port, log_level="warning")


if __name__ == "__main__":
    _demo()
