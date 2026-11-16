#!/bin/sh
# Starts ShelfServer in the background. Its settings come from shelf.env beside this script (kept out of git):
#   SHELF_LISTEN=<tailscale-ip>:8781   SHELF_LIBRARY=<folder with index/ and packs/>   SHELF_TEAM_FILE=<team.json>
# Data (published maps and props): ./data. Log: server.log (the previous one: server.log.1). Pid: server.pid.
cd "$(dirname "$0")" || exit 1
if [ -f server.pid ] && kill -0 "$(cat server.pid)" 2>/dev/null; then echo "already running (pid $(cat server.pid))"; exit 0; fi
[ -f shelf.env ] || { echo "write shelf.env first (see the top of this script)"; exit 1; }
. ./shelf.env
[ -f server.log ] && mv server.log server.log.1
# a small server (a NAS with 2 GB, say): the .NET heap is held to 256 MB
export DOTNET_GCHeapHardLimit=0x10000000 DOTNET_gcServer=0 SHELF_TEAM_FILE
nohup ./bin/ShelfServer --listen "${SHELF_LISTEN:?}" ${SHELF_LIBRARY:+--library "$SHELF_LIBRARY"} --store "$PWD/data" >> server.log 2>&1 &
echo $! > server.pid
echo "started (pid $(cat server.pid)) on http://$SHELF_LISTEN"
