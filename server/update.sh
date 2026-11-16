#!/bin/sh
# Replaces the server program and restarts it, keeping data/, shelf.env and the logs:
#   ./update.sh <new ShelfServer program>
# The old program is kept as bin/ShelfServer.old; ./update.sh bin/ShelfServer.old goes back to it.
cd "$(dirname "$0")" || exit 1
new="${1:?usage: update.sh <new ShelfServer program>}"
[ -f "$new" ] || { echo "no such file: $new"; exit 1; }
chmod +x "$new"
"$new" --version >/dev/null 2>&1 || { echo "$new does not run here (wrong platform?)"; exit 1; }
cp "$new" bin/ShelfServer.next
./stop.sh
[ -f bin/ShelfServer ] && mv bin/ShelfServer bin/ShelfServer.old
mv bin/ShelfServer.next bin/ShelfServer
./start.sh
echo "now ShelfServer $(bin/ShelfServer --version)"
