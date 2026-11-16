#!/bin/sh
cd "$(dirname "$0")" || exit 1
[ -f server.pid ] || { echo "not running"; exit 0; }
kill "$(cat server.pid)" 2>/dev/null && echo "stopped" || echo "was not running"
rm -f server.pid
