#!/bin/zsh
# Hot-reload the mod + agent without losing the colony: save, restart RimWorld, load, restart the agent.
set -u
cd "$(dirname "$0")/.."
rpc() { curl -s -m 60 localhost:8765/rpc -d "{\"method\":\"$1\",\"params\":${2:-{\}}}"; }
pkill -f "bin/rimagent play" 2>/dev/null; pkill -f "script/start.sh" 2>/dev/null
if rpc game.status | grep -q '"playing"'; then
  echo "saving rimagent-reload…"; rpc game.save '{"name":"rimagent-reload"}' >/dev/null; sleep 2; RESUME=1
else RESUME=0; fi
script/restart-game.sh >/dev/null
echo "waiting for RimBridge…"; for i in {1..120}; do sleep 3; curl -s -m 2 localhost:8765/health >/dev/null && break; done
sleep 5
if [[ $RESUME == 1 ]]; then
  echo "loading rimagent-reload…"; rpc game.load '{"name":"rimagent-reload"}' >/dev/null
  for i in {1..90}; do sleep 3; rpc game.status | grep -q '"playing"' && break; done
fi
exec script/start.sh "$@"
