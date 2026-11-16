#!/usr/bin/env bash
# Builds ShelfServer self-contained (the server needs no .NET) and copies it with start.sh and stop.sh over ssh:
#   server/deploy.sh <ssh-host>:<folder> [rid, default linux-arm64]
# A running server is restarted with the new program (update.sh). The first time: write <folder>/shelf.env (see
# start.sh), then ./start.sh there.
set -euo pipefail
target="${1:?usage: deploy.sh <ssh-host>:<folder> [rid]}"; rid="${2:-linux-arm64}"
host="${target%%:*}"; dir="${target#*:}"
here="$(cd "$(dirname "$0")" && pwd)"
out="${HOME}/.cache/asset-shelf/scratch/shelf-server-publish"
rm -rf "${out:?}"
dotnet publish "$here/ShelfServer" -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o "$out" -v q -nologo
ssh "$host" "mkdir -p '$dir/bin' '$dir/data'"
scp -q "$here/start.sh" "$here/stop.sh" "$here/update.sh" "$host:$dir/"
scp -q "$out/ShelfServer" "$host:$dir/bin/ShelfServer.new"
# a first install only puts it there; after that update.sh restarts it
ssh "$host" "cd '$dir' && chmod +x start.sh stop.sh update.sh && if [ -f bin/ShelfServer ]; then ./update.sh bin/ShelfServer.new && rm bin/ShelfServer.new; else mv bin/ShelfServer.new bin/ShelfServer; fi"
rm -rf "${out:?}"
echo "deployed to $target"
