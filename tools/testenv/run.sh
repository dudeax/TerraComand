#!/usr/bin/env bash
# Runs scripted scenarios in the real tModLoader client on a virtual X display.
#
#   tools/testenv/run.sh <scenario.txt|dir>... [--build] [--timeout SECONDS] [--keep]
#
# All scenarios run in ONE game session (fresh world + character each, no restart in between).
# Results go to .testenv/runs/<timestamp>-<name>/ (summary.txt; per scenario: results.txt + screenshots);
# .testenv/runs/latest points at the newest run. Exit code 0 = all expectations passed.
# Tuning env vars: TC_SPEED (ticks per frame, default 10), TC_DRAW_EVERY (default 30), TC_SHOT_FRAMES (default 10).
# Uses its own save directory (.testenv), so real characters/worlds are never touched.
# For multiplayer see run-mp.sh.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
source "$ROOT/tools/testenv/common.sh"

SCENARIOS=(); BUILD=0; TIMEOUT=420; KEEP=0
while [ $# -gt 0 ]; do
  case "$1" in
    --build) BUILD=1 ;;
    --timeout) TIMEOUT="$2"; shift ;;
    --keep) KEEP=1 ;;
    *) if [ -d "$1" ]; then for f in "$1"/*.txt; do SCENARIOS+=("$(realpath "$f")"); done
       else SCENARIOS+=("$(realpath "$1")"); fi ;;
  esac
  shift
done
[ ${#SCENARIOS[@]} -gt 0 ] || { echo "usage: $0 <scenario.txt|dir>... [--build] [--timeout s] [--keep]"; exit 2; }
for f in "${SCENARIOS[@]}"; do [ -f "$f" ] || { echo "no such scenario: $f"; exit 2; }; done

[ $BUILD = 1 ] && { build_mod || exit 1; }
[ -f "$TMOD" ] || { echo "no $TMOD - run with --build"; exit 1; }
ensure_display
prepare_savedir "$ENVD"
ensure_baseline || exit 1

if [ ${#SCENARIOS[@]} -eq 1 ]; then NAME="$(basename "${SCENARIOS[0]}" .txt)"; else NAME="suite"; fi
OUT="$(new_run_dir "$NAME")"
rm -f "$LOGDIR"/client*.log

( cd "$TML" && env "${CLIENT_ENV[@]}" \
  TC_AUTOTEST="$(IFS=:; echo "${SCENARIOS[*]}")" TC_OUT="$OUT" TC_WORLD="$ENVD/Worlds/tc_run.wld" TC_BASELINE="$BASELINE" \
  "$DOTNET" tModLoader.dll -nosteam -tmlsavedirectory "$ENVD" > "$OUT/stdout.log" 2>&1 ) &
CLIENT=$!
START=$(date +%s); LAST_GRAB=0
echo "Running ${#SCENARIOS[@]} scenario(s) -> $OUT"
while :; do
  sleep 1
  NOW=$(date +%s); EL=$((NOW - START))
  [ -f "$OUT/DONE" ] && break
  if ! kill -0 "$CLIENT" 2>/dev/null; then echo "client exited early"; break; fi
  [ $EL -gt "$TIMEOUT" ] && { echo "timeout after ${TIMEOUT}s"; break; }
  if grep -qs "FATAL" "$LOGDIR"/client*.log; then echo "game crashed (FATAL in client log)"; grab "$OUT/_crash.png"; break; fi
  # Without ~/.steam/sdk64/steamclient.so, -nosteam mode shows a blocking notice before mods load: press "Continue".
  if grep -qs "Steam Game Server failed" "$LOGDIR"/client*.log && ! grep -qs "Sandboxing" "$LOGDIR"/client*.log; then
    xdo mousemove $((W / 2 - 165)) $((H - 93)) sleep 0.2 mousedown 1 sleep 0.4 mouseup 1
  fi
  if [ $((NOW - LAST_GRAB)) -ge 15 ]; then grab "$OUT/_progress.png"; LAST_GRAB=$NOW; fi
done
sleep 1
grab "$OUT/_final_display.png"
if [ $KEEP = 1 ]; then echo "client left running (pid $CLIENT, display $DISP)"; else stop_pid "$CLIENT"; fi
cp "$LOGDIR"/client*.log "$OUT/" 2>/dev/null
echo "---- results ($(( $(date +%s) - START ))s)"
if [ ${#SCENARIOS[@]} -eq 1 ]; then
  cat "$OUT/results.txt" 2>/dev/null || echo "(no results.txt - see $OUT/client.log and _progress.png)"
else
  grep -hE "^\[FAIL\]" "$OUT"/*/results.txt 2>/dev/null
  cat "$OUT/summary.txt" 2>/dev/null || echo "(no summary.txt - see $OUT/client.log and _progress.png)"
fi
grep -hE "ERROR|Exception" "$OUT"/client*.log 2>/dev/null | grep -v "Steam Game Server\|Engine initialization\|Silently Caught\|/proc/" | head -5
[ "$(cat "$OUT/DONE" 2>/dev/null)" = "PASS" ]
