#!/usr/bin/env bash
# Multiplayer test: a dedicated server plus N scripted clients, all with the mod loaded.
#
#   tools/testenv/run-mp.sh <mp-scenario-dir> [--build] [--timeout SECONDS]
#
# The directory holds server.txt (setup script run once on the server before clients join) and one script per
# client (a.txt, b.txt, ...). Clients coordinate with `sync <label>` barriers. Each client has its own save dir
# and character (TCTestA, TCTestB, ...). Multiplayer runs at normal speed (no fast-forward).
# Results: .testenv/runs/latest/{server,a,b,...}/results.txt + screenshots.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
source "$ROOT/tools/testenv/common.sh"

DIR=""; BUILD=0; TIMEOUT=600; PORT="${TC_PORT:-7790}"
while [ $# -gt 0 ]; do
  case "$1" in
    --build) BUILD=1 ;;
    --timeout) TIMEOUT="$2"; shift ;;
    *) DIR="$(realpath "$1")" ;;
  esac
  shift
done
[ -f "$DIR/server.txt" ] || { echo "usage: $0 <dir with server.txt + client scripts> [--build] [--timeout s]"; exit 2; }
CLIENTS=(); for f in "$DIR"/*.txt; do [ "$(basename "$f")" = server.txt ] || CLIENTS+=("$(basename "$f" .txt)"); done

[ $BUILD = 1 ] && { build_mod || exit 1; }
ensure_display
prepare_savedir "$ENVD"          # also used to generate the baseline
ensure_baseline || exit 1

OUT="$(new_run_dir "mp-$(basename "$DIR")")"
SYNC="$OUT/sync"; mkdir -p "$SYNC" "$OUT/server"
rm -f "$LOGDIR"/client*.log "$LOGDIR"/server*.log

# ---- server
SD="$ENVD/mp-server"
prepare_savedir "$SD"
cp "$BASELINE" "$SD/Worlds/tc_mp.wld"; cp "${BASELINE%.wld}.twld" "$SD/Worlds/tc_mp.twld" 2>/dev/null
cat > "$SD/serverconfig.txt" <<CFG
world=$SD/Worlds/tc_mp.wld
maxplayers=8
port=$PORT
password=
CFG
# Server console input comes from a FIFO so we can send it `tc script` once it's up.
FIFO="$OUT/server/console"; mkfifo "$FIFO"
( cd "$TML" && "$DOTNET" tModLoader.dll -server -nosteam -config "$SD/serverconfig.txt" -tmlsavedirectory "$SD" \
  < "$FIFO" > "$OUT/server/stdout.log" 2>&1 ) &
SERVER=$!
exec 7>"$FIFO"   # hold the FIFO open for writing for the server's lifetime
# $SERVER is the wrapper subshell; the server process itself is found by its save dir.
stop_server() { pkill -TERM -f "tmlsavedirectory $SD\$" 2>/dev/null; sleep 3; pkill -KILL -f "tmlsavedirectory $SD\$" 2>/dev/null; kill -KILL "$SERVER" 2>/dev/null; }
START=$(date +%s)
echo "Server starting (port $PORT)..."
SENT=0
while [ ! -f "$OUT/server/READY" ]; do
  sleep 1
  if [ $SENT = 0 ] && grep -qs "Server started" "$OUT/server/stdout.log"; then
    echo "tc script $DIR/server.txt $OUT/server" >&7; SENT=1
  fi
  kill -0 "$SERVER" 2>/dev/null || { echo "server exited"; tail -20 "$OUT/server/stdout.log"; exit 1; }
  [ $(( $(date +%s) - START )) -gt 180 ] && { echo "server not ready after 180s"; cat "$OUT/server/results.txt"; stop_server; exit 1; }
done
echo "Server ready after $(( $(date +%s) - START ))s; launching ${#CLIENTS[@]} clients: ${CLIENTS[*]}"

# ---- clients
# Each client gets its own virtual display (:100, :101, ...) so notices can be clicked per window.
PIDS=(); DISPS=(); n=0
for c in "${CLIENTS[@]}"; do
  CD="$ENVD/mp-$c"; prepare_savedir "$CD"; mkdir -p "$OUT/$c"
  NAME="TCTest$(echo "$c" | tr a-z A-Z)"
  CDISP=":$((100 + n))"; n=$((n + 1)); ensure_display "$CDISP"; DISPS+=("$CDISP")
  ( cd "$TML" && exec env "${CLIENT_ENV[@]}" DISPLAY="$CDISP" TC_AUTOTEST="$DIR/$c.txt" TC_OUT="$OUT/$c" TC_JOIN="127.0.0.1:$PORT" \
      TC_PLAYER="$NAME" TC_CLIENT_ID="$c" TC_SYNC_DIR="$SYNC" TC_PEERS="${#CLIENTS[@]}" TC_SPEED=1 TC_DRAW_EVERY="${TC_DRAW_EVERY:-4}" \
      "$DOTNET" tModLoader.dll -nosteam -tmlsavedirectory "$CD" > "$OUT/$c/stdout.log" 2>&1 ) &
  PIDS+=($!)
  sleep 2
done

while :; do
  sleep 2
  # The server holds the Steam game-server API, so clients show the -nosteam notice: click it until their driver starts.
  for i in "${!CLIENTS[@]}"; do [ -f "$OUT/${CLIENTS[$i]}/results.txt" ] || click_continue "${DISPS[$i]}"; done
  all=1; for c in "${CLIENTS[@]}"; do [ -f "$OUT/$c/DONE" ] || all=0; done
  [ $all = 1 ] && break
  for i in "${!PIDS[@]}"; do kill -0 "${PIDS[$i]}" 2>/dev/null || { echo "client ${CLIENTS[$i]} exited early"; all=2; }; done
  [ $all = 2 ] && break
  [ $(( $(date +%s) - START )) -gt "$TIMEOUT" ] && { echo "timeout after ${TIMEOUT}s"; break; }
  grep -qs "FATAL" "$LOGDIR"/*.log && { echo "a game process crashed (FATAL in logs)"; break; }
done
for i in "${!CLIENTS[@]}"; do DISP="${DISPS[$i]}" grab "$OUT/${CLIENTS[$i]}/_final_display.png"; done
for p in "${PIDS[@]}"; do stop_pid "$p"; done
echo "exit" >&7 2>/dev/null; sleep 2
stop_server; exec 7>&-
cp "$LOGDIR"/*.log "$OUT/" 2>/dev/null

echo "---- results ($(( $(date +%s) - START ))s)"
ok=1
for c in server "${CLIENTS[@]}"; do
  echo "== $c"; grep -hE "^\[(FAIL|DONE)\]" "$OUT/$c/results.txt" 2>/dev/null || { echo "(no results)"; ok=0; }
  [ "$(cat "$OUT/$c/DONE" 2>/dev/null)" = "PASS" ] || ok=0
done
# (ObjectDisposedException in CompressTileBlock = server still sending sections while clients disconnect at teardown)
grep -hE "ERROR|Exception" "$OUT"/*.log 2>/dev/null | grep -v "Steam Game Server\|Engine initialization\|Silently Caught\|/proc/\|ObjectDisposed\|closed Stream" | sort -u | head -8
[ $ok = 1 ]
