# rimagent, developer notes (agent-readable)

Two halves, the mod as its own repo since 2026-09-16, split further into bridge + optional add-on since 2026-09-16:
- `mod/` **RimBridge** (C#, RimWorld 1.6, Harmony): loopback HTTP bridge exposing the engine — bridge only, no
  autonomy layer. Its own repo, github.com/zorrobyte/rimbridge, vendored here as a git submodule —
  `git submodule update --init` after cloning, and commit/push mod-side changes from inside `mod/` before bumping
  the pointer here. `Rpc.RegisterAssembly` and `RimBridge.Server.Hooks` (manual-touch, research-finished,
  state.summary contributors) let an add-on mod that loads after RimBridge register its own RPCs and hook into
  core ui.*/ledger/state behavior without RimBridge knowing it exists.
- `mod-steward/` **RimBridge: Steward** (C#, same toolchain, packageId `zorrobyte.rimagent-steward`): optional
  add-on, lives in *this* repo (not the bridge repo) since it's rimagent-specific policy, not generic bridge
  infrastructure. References `mod/1.6/Assemblies/RimBridge.dll`, so `script/build.sh` builds `mod/` first. See
  "Steward"/"Orders" below — same behavior as before the split, just a separate mod (`StewardMod.cs`, settings
  under `StewardMod.Settings` instead of nested on RimBridge's own settings).
- Symlink both `mod/` and `mod-steward/` into the RimWorld Mods folder (as `RimBridge` and `RimBridgeSteward`).
  Build `script/build.sh` (needs `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`), then `script/restart-game.sh`
  (DLLs load at startup only; always launch via Steam so Workshop Harmony loads).
- `agent/` **rimagent** (Python, uv): the brain. `script/start.sh` = launch game if needed + `rimagent play` + dashboard.
- `brain/` what the agent authors: `skills/*.md` (frontmatter name/description/tags/always), `tools/*.py`,
  `watchers/*.py` (hot-loaded), `memory/notebook.md` (per colony), `memory/journal.md` (cross-game), `scores.jsonl`.
  The runner commits `brain/` per episode; the agent can `brain_revert`.
- `knowledge/` wiki dump + BM25 (`rimagent seed`), `source-1.6/` (ilspycmd of the installed DLL), `source-legacy/`.

## Bridge
`POST 127.0.0.1:8765/rpc {"method":"state.summary","params":{}}`; `GET /health /methods /events?since= /screenshot?x=&z=&w=`.
Method groups: game.* state.* map.* ui.* engine.* defs.* dev.* anchor.* from `mod/` (see `[Rpc(name, doc)]` attributes
in `mod/Source`), plus steward.* from the optional `mod-steward/` add-on if it's loaded (`mod-steward/Source`).
All Verse work runs on the main thread via `MainThreadQueue` (drained in a `Root.Update` postfix); request threads
only parse/serialize. Never throw into Unity: every RPC error becomes `{ok:false,error}`. Namespaces `GameCtl`/`MapView`
avoid clashes with `Verse.Game`/`Verse.Map`.
- **Steward** (`steward.*`, `mod-steward/Source/Steward/`, namespace `RimBridge.Steward`): two vendored engines that run every tick
  without the LLM. `Scorer/` (Free Will port, MIT) writes work priorities for every *managed* colonist; `Stock/` (synchronous
  rewrite of Colony Manager Redux, MIT) keeps stock jobs (forestry, foraging, hunting, mining, production, livestock) at
  targets by designating work; `StewardRpc.cs` exposes status/enable/pawn/explain/posture/stock.*/settings/research,
  `StewardTuning.cs` holds posture deltas and the per-pawn managed gate, `StewardLedger.cs` emits `stock_stalled` /
  `stock_reached` / `posture_expired`. Both default ON and survive save/load. Rule: `ui.set_work` marks the pawn
  unmanaged before applying and returns `steward_managed: false` (otherwise the scorer would clobber the change);
  `steward.pawn managed=true` hands the pawn back. Nothing in steward.* marks the game assisted. Origins in
  `THIRD_PARTY_NOTICES.md`; keep vendored headers, add "modified for RimBridge" lines.
- **Orders** (`steward.orders*`, `mod-steward/Source/Steward/Orders/`): standing orders are deterministic reflexes that run from a
  MapComponentTick, staggered by id, never throw, budget-logged over 20 ms: `combat` (draft capable fighters to the rally
  rect, hold, release, then rescue), `rescue`, `unforbid`, `corpses`, `beds`, `policies`, `blueprints`, `fire`. One class per
  order (`Order_*.cs`, base `Order { Id, Label, Doc, IntervalTicks, Enabled, Run(Map) -> OrderReport, Explain() }`), registry
  and persisted state (enabled flags, rally rect, manual-touch cooldowns, last summary) in `StandingOrders.cs`, RPCs in
  `OrdersRpc.cs` (`steward.orders`, `.set`, `.rally`, `.explain`, `.run`). Manual-touch rule: `ui.draft/goto/attack`,
  forbid/unforbid, `ui.set_policies`, `ui.press` on a bed and `ui.job Rescue/TendPatient` record (id, tick) so the matching
  order skips that pawn/thing for a cooldown. Ledger kind `orders` (`combat_engaged`, `combat_released`, `rescue`, `corpses`,
  `blueprints_cancelled`, `fire`). The brain watchers that did the same from Python stay, but the runner marks them *superseded* (`registry.watcher_superseded`,
  table `watchers.SUPERSEDED_WATCHERS`, config `steward.orders.superseded_watchers`) and `watchers.run_all` skips them while
  their order is on and the mod answered `steward.orders.set`: their `ui.draft/goto/order/designate` calls would record manual
  touches that pause the order for the very pawns they move. `watcher_write`/`watcher_delete` lift the mark; the seeded
  doctrine tells the director to delete them.

## Agent
- `rimagent play [--max-days N] [--seeds a,b] [--no-pause] -v`, `rimagent think` (one step), `rimagent tools`, `rimagent llm "hi"`, `rimagent seed [--distill]`.
- Bridge methods auto-become tools `rw_<group>_<name>`; models send JSON args as strings → `registry.coerce_param`.
- Think step = fresh bounded conversation (`loop.think`), ends with `end_turn`/`end_episode`. Runner (`runner.py`) pauses
  the game while thinking, wakes on schedule / ledger event kinds / watcher alerts, autosaves daily, runs an improvement
  pass every N days and an episode reflection at the end, then starts the next seeded game.
- Dashboard `127.0.0.1:8770` streams `bus.py` events (see its docstring for the event contract).
- **Watchdog** (`watchdog.py`, `tools/watchdog.py`, `prompts/watchdog.md`, `roles.allow_watchdog`, config `watchdog:`):
  a second, more privileged self-correction stream. The improvement pass stays inside `brain/` (hot-reloadable text,
  safe by construction); the watchdog reads the **tool-call error stream** and patches the **project's own source**.
  Trigger: `Runner.maybe_start_watchdog(day)` on each in-game day rollover, gated by `watchdog.due()` — needs
  `every_hours` of wall clock **and** `min_errors` failed tool calls since the last pass (a clean error stream fires
  nothing), and never runs beside itself or an improvement pass. It runs on its own thread (`start_watchdog_thread`),
  never blocking play. Input: `recent_errors(bus, since_seq)` stitches each failed `tool_result` back to its
  `tool_call` args and the `think_start` it came from; `format_errors` groups repeats. Scope: **`mod/Source/**` and
  `agent/rimagent/**` only** — `safe_path()` rejects `..`, absolute escapes, symlink escapes, `.git`, `obj`/`bin`/
  `__pycache__`, and everything else (`brain/`, `config.local.yaml`, `knowledge/`, `mod/1.6/`). Tools (group
  `watchdog`, allowlisted exactly by `roles.allow_watchdog`, no `run_python`/`rpc`/`rw_*`/brain tools):
  `repo_read` `repo_list` `repo_grep` `repo_patch(path, content)` `repo_revert(path)` `watchdog_verify_python()`
  `watchdog_verify_mod()` `watchdog_commit(message)` `end_watchdog(summary, fixes, skipped)`, plus the read-only
  knowledge tools. Proof is enforced server-side: a patch marks its root unverified, and `watchdog_commit` refuses
  unless the matching verify tool ran, passed, and ran *after* the last patch to that root; it stages exactly the
  paths touched (never `-A`) and appends the co-author trailer itself.
  **It never deploys**: the verification build writes to `runs/watchdog-build/`, so `mod/1.6/Assemblies/` (the
  symlink the running game loaded) is untouched, the game is never restarted, and there is no push — verified fixes
  are committed locally and wait for a human to deploy at the next natural restart. Log: `brain/memory/watchdog_log.md`
  (append-only, one entry per pass), bus kind `watchdog`, dashboard tab "Watchdog".
- Tests: `cd agent && uv run pytest -q`; `cd mod/Tests && dotnet test` (PathParser only); `cd mod-steward/Tests && dotnet test`
  (Steward's pure logic: stock triggers, plant math, standing-order rules). Both Verse-free by construction.

## Conventions
- Log from C# via `BridgeLog` (`[RimBridge]` prefix). Watch `~/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`.
- Tool results are truncated (~8k chars); prefer narrow queries in tools and docs.
- Config: `config.yaml` (generic) + `config.local.yaml` (gitignored: your LLM endpoint). Seeds, cadence, speeds live there.
  `steward: {enabled, scorer, stock, orders: {enabled, off: [], superseded_watchers?: {stem: order id}}}` (all default true): the runner calls `steward.enable` and
  `steward.orders.set` at new_game/recover_game and when the dashboard toggles it; the situation packet gets a "Steward"
  block from `steward.status` (posture, stock rows, problems, orders line, `rally: none` when unset).
