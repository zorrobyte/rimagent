#!/bin/zsh
# One command: make sure RimWorld is running with RimBridge, then start the agent + dashboard.
#   script/start.sh              # play forever (episodes back to back)
#   script/start.sh --max-days 3 # shorter episodes
set -u
cd "$(dirname "$0")/.."
export PYTHONUNBUFFERED=1
if ! curl -s -m 2 localhost:8765/health >/dev/null; then
  if ! pgrep -f "RimWorldMac.app/Contents/MacOS/RimWorld" >/dev/null; then
    echo "launching RimWorld via Steam…"
    open "steam://rungameid/294100"
  fi
  echo "waiting for RimBridge on :8765…"
  for i in {1..120}; do sleep 3; curl -s -m 2 localhost:8765/health >/dev/null && break; done
fi
mkdir -p runs
log="runs/play-$(date +%Y%m%d-%H%M%S).log"
echo "agent log: $log ; dashboard: http://127.0.0.1:8770"
( sleep 4; open "http://127.0.0.1:8770" ) &
cd agent && exec uv run rimagent play -v "$@" 2>&1 | tee "../$log"
