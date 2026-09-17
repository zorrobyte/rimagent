#!/bin/zsh
# Build RimBridge, then the optional Steward add-on (references RimBridge.dll, must build second).
# Output lands in mod/1.6/Assemblies and mod-steward/1.6/Assemblies (both symlinked into the RimWorld Mods folder).
set -e
cd "$(dirname "$0")/.."
dotnet build mod/Source/RimBridge.csproj -c Release --nologo -v quiet "$@"
ls -la mod/1.6/Assemblies/RimBridge.dll
dotnet build mod-steward/Source/RimBridgeSteward.csproj -c Release --nologo -v quiet "$@"
ls -la mod-steward/1.6/Assemblies/RimBridgeSteward.dll
