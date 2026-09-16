# rimagent — Qwen plays RimWorld and teaches itself

Two halves in one repo:
- `mod/` **RimBridge** (C#, RimWorld 1.6, Harmony): loopback HTTP bridge exposing the engine. Symlinked into the
  RimWorld Mods folder as `RimBridge`. Build `script/build.sh` (needs `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`),
  then `script/restart-game.sh` (DLLs load at startup only; always launch via Steam so Workshop Harmony loads).
- `agent/` **rimagent** (Python, uv): the brain. `script/start.sh` = launch game if needed + `rimagent play` + dashboard.
- `brain/` what the agent authors: `skills/*.md` (frontmatter name/description/tags/always), `tools/*.py`,
  `watchers/*.py` (hot-loaded), `memory/notebook.md` (per colony), `memory/journal.md` (cross-game), `scores.jsonl`.
  The runner commits `brain/` per episode; the agent can `brain_revert`.
- `knowledge/` wiki dump + BM25 (`rimagent seed`), `source-1.6/` (ilspycmd of the installed DLL), `source-legacy/`.

## Bridge
`POST 127.0.0.1:8765/rpc {"method":"state.summary","params":{}}`; `GET /health /methods /events?since= /screenshot?x=&z=&w=`.
Method groups: game.* state.* map.* ui.* engine.* defs.* dev.* — see `[Rpc(name, doc)]` attributes in `mod/Source`.
All Verse work runs on the main thread via `MainThreadQueue` (drained in a `Root.Update` postfix); request threads
only parse/serialize. Never throw into Unity: every RPC error becomes `{ok:false,error}`. Namespaces `GameCtl`/`MapView`
avoid clashes with `Verse.Game`/`Verse.Map`.

## Agent
- `rimagent play [--max-days N] [--seeds a,b] [--no-pause] -v`, `rimagent think` (one step), `rimagent tools`, `rimagent llm "hi"`, `rimagent seed [--distill]`.
- Bridge methods auto-become tools `rw_<group>_<name>`; models send JSON args as strings → `registry.coerce_param`.
- Think step = fresh bounded conversation (`loop.think`), ends with `end_turn`/`end_episode`. Runner (`runner.py`) pauses
  the game while thinking, wakes on schedule / ledger event kinds / watcher alerts, autosaves daily, runs an improvement
  pass every N days and an episode reflection at the end, then starts the next seeded game.
- Dashboard `127.0.0.1:8770` streams `bus.py` events (see its docstring for the event contract).
- Tests: `cd agent && uv run pytest -q`; `cd mod/Tests && dotnet test` (PathParser only; keep it Verse-free).

## Conventions
- Log from C# via `BridgeLog` (`[RimBridge]` prefix). Watch `~/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`.
- Tool results are truncated (~8k chars); prefer narrow queries in tools and docs.
- Config: `config.yaml` (LLM endpoint chaos-srv, seeds, cadence). Do not change the chaos-srv server config from here.
