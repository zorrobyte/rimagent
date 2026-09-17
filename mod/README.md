# RimBridge

Local HTTP bridge that exposes RimWorld's engine, map and player controls to an external agent. Listens on
`127.0.0.1:8765` only. Built for [rimagent](https://github.com/zorrobyte/rimagent) (an LLM agent that plays
RimWorld), but usable standalone by anything that wants programmatic control of a running game.

`POST /rpc {"method": "state.summary", "params": {}}`, plus `GET /health /methods /events?since= /screenshot?x=&z=&w=`.
Method groups: `game.* state.* map.* ui.* engine.* defs.* dev.* anchor.*` — see the `[Rpc(name, doc)]` attributes
under `Source/`. This repo is the bridge only — no autonomy layer, no scoring, no automation. `Rpc.RegisterAssembly`
lets another mod that loads after RimBridge (referencing `RimBridge.dll`) register its own RPC methods onto the
same dispatcher/HTTP server, for exactly that kind of add-on.

## Build

Requires .NET SDK and the RimWorld 1.6 managed assemblies.

```
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec   # or wherever dotnet lives
dotnet build Source/RimBridge.csproj
```

Symlink or copy this repo into your RimWorld `Mods/` folder as `RimBridge`, enable it (and its Harmony
dependency) in the mod list, and restart the game (managed DLLs only load at startup).

## License

MIT — see [LICENSE](LICENSE).
