#!/usr/bin/env bash
# Runs a scripted scenario in the real tModLoader client on a virtual X display.
#
#   tools/testenv/run.sh <scenario.txt|dir>... [--build] [--timeout SECONDS] [--keep]
#
# All scenarios run in ONE game session (fresh world + character each, no restart in between).
# Results go to .testenv/runs/<timestamp>-<name>/ (summary.txt; per scenario: results.txt + screenshots);
# .testenv/runs/latest points at the newest run. Exit code 0 = all expectations passed.
# Tuning env vars: TC_SPEED (ticks per frame, default 10), TC_DRAW_EVERY (default 30), TC_SHOT_FRAMES (default 10).
# Uses its own save directory (.testenv), so real characters/worlds are never touched.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TML="${TML_DIR:-/mnt/media/SteamLibrary/tModLoader}"
ENVD="$ROOT/.testenv"
DISP="${TC_DISPLAY:-:99}"
XVFB_ROOT="${XVFB_ROOT:-$HOME/.local/opt/xvfb/root/usr}"
XDO_ROOT="${XDOTOOL_ROOT:-$HOME/.local/opt/xdotool/root/usr}"
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
DOTNET="$(command -v dotnet)"
W=1280; H=720

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

xdo() { LD_LIBRARY_PATH="$XDO_ROOT/lib/x86_64-linux-gnu" DISPLAY="$DISP" "$XDO_ROOT/bin/xdotool" "$@"; }
grab() { DISPLAY="$DISP" ffmpeg -loglevel error -y -f x11grab -video_size ${W}x${H} -i "$DISP" -frames:v 1 "$1" 2>/dev/null; }

# ---- build
if [ $BUILD = 1 ]; then
  (cd "$ROOT/TerraCommand" && "$DOTNET" build -c Release -p:TmlDir="$TML/" 2>&1 | grep -E "error|Build succeeded" | sort -u) || exit 1
fi
TMOD="$HOME/.local/share/Terraria/tModLoader/Mods/TerraCommand.tmod"
[ -f "$TMOD" ] || { echo "no $TMOD - run with --build"; exit 1; }

# ---- virtual display
if [ ! -e "/tmp/.X11-unix/X${DISP#:}" ]; then
  (cd "$XVFB_ROOT/bin" && LD_LIBRARY_PATH="$XVFB_ROOT/lib/x86_64-linux-gnu" nohup ./Xvfb-local "$DISP" -screen 0 ${W}x${H}x24 >/dev/null 2>&1 &)
  sleep 2
fi

# ---- isolated save dir
pkill -KILL -f "tmlsavedirectory $ENVD" 2>/dev/null; sleep 1
mkdir -p "$ENVD"/{Mods,Worlds,Players,baseline,runs}
cp "$TMOD" "$ENVD/Mods/"
echo '["TerraCommand"]' > "$ENVD/Mods/enabled.json"
python3 - "$ENVD/config.json" $W $H <<'PY'
import json, os, sys
p, w, h = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
d = json.load(open(p)) if os.path.exists(p) else {}
d.update({"DisplayWidth": w, "DisplayHeight": h, "Fullscreen": False, "WindowMaximized": False, "WindowBorderless": True,
          "VolumeSound": 0.0, "VolumeAmbient": 0.0, "VolumeMusic": 0.0, "Language": "en-US",
          "SeenFirstLaunchModderWelcomeMessage": True, "BetaUpgradeWelcomed144": True, "Autosave": False})
json.dump(d, open(p, "w"), indent=2)
PY
rm -f "$ENVD"/Players/TCTest*

# ---- baseline world (generated once by the dedicated server, with the mod loaded)
if [ ! -f "$ENVD/baseline/tc_test.wld" ]; then
  echo "Generating baseline world (one-time, ~3 min)..."
  cat > "$ENVD/baseline/serverconfig.txt" <<CFG
world=$ENVD/baseline/tc_test.wld
autocreate=1
worldname=tc_test
difficulty=0
seed=12345
port=7798
CFG
  ( cd "$TML" && (while ! grep -q "Server started" "$ENVD/baseline/gen.log" 2>/dev/null; do sleep 2; done; echo exit) | \
    timeout 600 "$DOTNET" tModLoader.dll -server -nosteam -tmlsavedirectory "$ENVD" -config "$ENVD/baseline/serverconfig.txt" > "$ENVD/baseline/gen.log" 2>&1 )
  [ -f "$ENVD/baseline/tc_test.wld" ] || { echo "world generation failed, see $ENVD/baseline/gen.log"; exit 1; }
fi

# ---- run
if [ ${#SCENARIOS[@]} -eq 1 ]; then NAME="$(basename "${SCENARIOS[0]}" .txt)"; else NAME="suite"; fi
OUT="$ENVD/runs/$(date +%Y%m%d-%H%M%S)-$NAME"
mkdir -p "$OUT"; ln -sfn "$OUT" "$ENVD/runs/latest"
LOGDIR="$TML/tModLoader-Logs"
rm -f "$LOGDIR"/client*.log

( cd "$TML" && DISPLAY="$DISP" LIBGL_ALWAYS_SOFTWARE=1 SDL_AUDIODRIVER=disk SDL_DISKAUDIOFILE=/dev/null \
  TC_AUTOTEST="$(IFS=:; echo "${SCENARIOS[*]}")" TC_OUT="$OUT" TC_WORLD="$ENVD/Worlds/tc_run.wld" TC_BASELINE="$ENVD/baseline/tc_test.wld" \
  exec "$DOTNET" tModLoader.dll -nosteam -tmlsavedirectory "$ENVD" > "$OUT/stdout.log" 2>&1 ) &
CLIENT=$!
START=$(date +%s); LAST_GRAB=0

stop_client() {
  # The client often deadlocks on SIGTERM under Xvfb, so escalate to SIGKILL.
  kill -TERM "$CLIENT" 2>/dev/null
  for _ in $(seq 10); do kill -0 "$CLIENT" 2>/dev/null || break; sleep 1; done
  kill -KILL "$CLIENT" 2>/dev/null
  wait "$CLIENT" 2>/dev/null
}
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
if [ $KEEP = 1 ]; then echo "client left running (pid $CLIENT, display $DISP)"; else stop_client; fi
cp "$LOGDIR"/client*.log "$OUT/" 2>/dev/null
echo "---- results ($(( $(date +%s) - START ))s)"
if [ ${#SCENARIOS[@]} -eq 1 ]; then
  cat "$OUT/results.txt" 2>/dev/null || echo "(no results.txt - see $OUT/client.log and _progress.png)"
else
  grep -hE "^\[(FAIL)\]" "$OUT"/*/results.txt 2>/dev/null
  cat "$OUT/summary.txt" 2>/dev/null || echo "(no summary.txt - see $OUT/client.log and _progress.png)"
fi
grep -hE "ERROR|Exception" "$OUT"/client*.log 2>/dev/null | grep -v "Steam Game Server\|Engine initialization" | head -5
[ "$(cat "$OUT/DONE" 2>/dev/null)" = "PASS" ]
