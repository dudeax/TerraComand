# Shared helpers for run.sh / run-mp.sh. Source after setting ROOT.
TML="${TML_DIR:-/mnt/media/SteamLibrary/tModLoader}"
ENVD="$ROOT/.testenv"
DISP="${TC_DISPLAY:-:99}"
XVFB_ROOT="${XVFB_ROOT:-$HOME/.local/opt/xvfb/root/usr}"
XDO_ROOT="${XDOTOOL_ROOT:-$HOME/.local/opt/xdotool/root/usr}"
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
DOTNET="$(command -v dotnet)"
W=1280; H=720
TMOD="$HOME/.local/share/Terraria/tModLoader/Mods/TerraCommand.tmod"
BASELINE="$ENVD/baseline/tc_test.wld"
LOGDIR="$TML/tModLoader-Logs"
# Env for game clients: virtual display, software GL, fake audio device.
CLIENT_ENV=(DISPLAY="$DISP" LIBGL_ALWAYS_SOFTWARE=1 SDL_AUDIODRIVER=disk SDL_DISKAUDIOFILE=/dev/null)

xdo() { LD_LIBRARY_PATH="$XDO_ROOT/lib/x86_64-linux-gnu" DISPLAY="$DISP" "$XDO_ROOT/bin/xdotool" "$@"; }
grab() { DISPLAY="$DISP" ffmpeg -loglevel error -y -f x11grab -video_size ${W}x${H} -i "$DISP" -frames:v 1 "$1" 2>/dev/null; }

build_mod() {
  (cd "$ROOT/TerraCommand" && "$DOTNET" build -c Release -p:TmlDir="$TML/" 2>&1 | grep -E " error |Build succeeded" | sort -u | tee /dev/stderr | grep -q "Build succeeded")
}

# ensure_display [DISPLAY]: start an Xvfb on that display (default $DISP) if none is running.
ensure_display() {
  local d="${1:-$DISP}"
  if [ ! -e "/tmp/.X11-unix/X${d#:}" ]; then
    (cd "$XVFB_ROOT/bin" && LD_LIBRARY_PATH="$XVFB_ROOT/lib/x86_64-linux-gnu" nohup ./Xvfb-local "$d" -screen 0 ${W}x${H}x24 >/dev/null 2>&1 &)
    sleep 2
  fi
}

# click_continue [DISPLAY]: press "Continue" on tModLoader's -nosteam notice (holds the button so slow frames see it).
click_continue() {
  DISP="${1:-$DISP}" xdo mousemove $((W / 2 - 165)) $((H - 93)) sleep 0.2 mousedown 1 sleep 0.4 mouseup 1
}

# prepare_savedir DIR: isolated tModLoader save dir with only this mod enabled and test-friendly settings.
prepare_savedir() {
  local d="$1"
  pkill -KILL -f "tmlsavedirectory $d\$" 2>/dev/null
  mkdir -p "$d"/{Mods,Worlds,Players}
  cp "$TMOD" "$d/Mods/"
  echo '["TerraCommand"]' > "$d/Mods/enabled.json"
  python3 - "$d/config.json" $W $H <<'PY'
import json, os, sys
p, w, h = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
d = json.load(open(p)) if os.path.exists(p) else {}
d.update({"DisplayWidth": w, "DisplayHeight": h, "Fullscreen": False, "WindowMaximized": False, "WindowBorderless": True,
          "VolumeSound": 0.0, "VolumeAmbient": 0.0, "VolumeMusic": 0.0, "Language": "en-US",
          "SeenFirstLaunchModderWelcomeMessage": True, "BetaUpgradeWelcomed144": True, "Autosave": False})
json.dump(d, open(p, "w"), indent=2)
PY
  rm -rf "$d"/Players/TCTest*
}

# Baseline world: generated once by the dedicated server (fixed seed, mod loaded).
ensure_baseline() {
  [ -f "$BASELINE" ] && return 0
  echo "Generating baseline world (one-time, ~3 min)..."
  mkdir -p "$ENVD/baseline"
  cat > "$ENVD/baseline/serverconfig.txt" <<CFG
world=$BASELINE
autocreate=1
worldname=tc_test
difficulty=0
seed=12345
port=7798
CFG
  ( cd "$TML" && (while ! grep -q "Server started" "$ENVD/baseline/gen.log" 2>/dev/null; do sleep 2; done; echo exit) | \
    timeout 600 "$DOTNET" tModLoader.dll -server -nosteam -config "$ENVD/baseline/serverconfig.txt" -tmlsavedirectory "$ENVD" > "$ENVD/baseline/gen.log" 2>&1 )
  [ -f "$BASELINE" ] || { echo "world generation failed, see $ENVD/baseline/gen.log"; return 1; }
}

# stop_pid PID: the client often deadlocks on SIGTERM under Xvfb, so escalate to SIGKILL.
stop_pid() {
  kill -TERM "$1" 2>/dev/null
  for _ in $(seq 10); do kill -0 "$1" 2>/dev/null || break; sleep 1; done
  kill -KILL "$1" 2>/dev/null
  { wait "$1"; } 2>/dev/null   # (swallows the shell's "Segmentation fault" job notice from the forced kill)
}

# new_run_dir NAME -> prints the output dir and points runs/latest at it
new_run_dir() {
  local out="$ENVD/runs/$(date +%Y%m%d-%H%M%S)-$1"
  mkdir -p "$out"; ln -sfn "$out" "$ENVD/runs/latest"; echo "$out"
}
