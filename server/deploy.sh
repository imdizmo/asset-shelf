#!/usr/bin/env bash
# Builds ShelfServer self-contained (the server needs no .NET) and copies it with start.sh and stop.sh over ssh:
#   server/deploy.sh <ssh-host>:<folder> [rid, default linux-arm64]
# Then, on the server: write <folder>/shelf.env once (see start.sh), and ./stop.sh; ./start.sh
set -euo pipefail
target="${1:?usage: deploy.sh <ssh-host>:<folder> [rid]}"; rid="${2:-linux-arm64}"
host="${target%%:*}"; dir="${target#*:}"
here="$(cd "$(dirname "$0")" && pwd)"
out="${HOME}/.cache/asset-shelf/scratch/shelf-server-publish"
rm -rf "${out:?}"
dotnet publish "$here/ShelfServer" -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o "$out" -v q -nologo
ssh "$host" "mkdir -p '$dir/bin' '$dir/data'"
scp -q "$out/ShelfServer" "$host:$dir/bin/ShelfServer.new"
scp -q "$here/start.sh" "$here/stop.sh" "$host:$dir/"
ssh "$host" "cd '$dir' && chmod +x start.sh stop.sh bin/ShelfServer.new && mv bin/ShelfServer.new bin/ShelfServer"
rm -rf "${out:?}"
echo "deployed to $target"
