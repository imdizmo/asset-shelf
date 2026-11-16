#!/usr/bin/env bash
# The Git LFS check: a real git-lfs pushes, clones, locks and unlocks against the server on loopback, and a caller who
# is not on the team is refused without being asked for a password.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
s="${HOME}/.cache/asset-shelf/scratch/server-lfs-test"
rm -rf "${s:?}"; mkdir -p "$s/store"
echo '{"version":1,"members":[{"login":"me@x","name":"Me","role":"member"},{"login":"obs@x","name":"Obs","role":"observer"}]}' > "$s/team.json"
dotnet build -c Release "$root/server/ShelfServer" -v q -nologo >/dev/null
port=18791; url="http://127.0.0.1:$port/lfs/game"
start() { SHELF_DEV_LOGIN="$1" SHELF_DEV_NAME="$1" "$root/server/ShelfServer/bin/Release/net10.0/ShelfServer" --listen 127.0.0.1:$port --store "$s/store" --team "$s/team.json" >>"$s/log" 2>&1 & pid=$!; for _ in $(seq 50); do curl -s "http://127.0.0.1:$port/team/whoami" >/dev/null && return; sleep 0.1; done; }
stop() { kill $pid; wait $pid 2>/dev/null || true; }
trap 'kill $pid 2>/dev/null || true' EXIT
# a credential helper that records being asked: it must never be
printf '#!/bin/sh\necho asked >> %s/asked\n' "$s" > "$s/cred.sh"; chmod +x "$s/cred.sh"
export GIT_TERMINAL_PROMPT=0 GIT_CONFIG_GLOBAL="$s/gitconfig" HOME_SAVE="$HOME"
git config -f "$s/gitconfig" credential.helper "$s/cred.sh"
git config -f "$s/gitconfig" user.name t; git config -f "$s/gitconfig" user.email t@x; git config -f "$s/gitconfig" init.defaultBranch main
git lfs install --skip-repo >/dev/null
start me@x
git init -q --bare "$s/remote.git"
git init -q "$s/a"; cd "$s/a"
git lfs track '*.uasset' >/dev/null; git config -f .lfsconfig lfs.url "$url"; git config -f .lfsconfig lfs.locksverify true
head -c 3000000 /dev/urandom > Big.uasset; echo small > Small.uasset
git add -A; git commit -qm one; git remote add origin "$s/remote.git"
git push -q origin main 2>&1 | grep -v '^Uploading\|^$' || true
n=$(find "$s/store/lfs/game/objects" -type f ! -name '.*' | wc -l); [[ $n == 2 ]] || { echo "FAIL push: $n objects"; exit 1; }; echo "ok push"
git clone -q "$s/remote.git" "$s/b" 2>&1 | grep -v '^$' || true
cmp Big.uasset "$s/b/Big.uasset" && echo "ok clone"
git lfs lock Big.uasset >/dev/null && echo "ok lock"
git lfs locks | grep -q Big.uasset && echo "ok list"
stop; start obs@x
(cd "$s/b" && git lfs locks | grep -q Big.uasset) && echo "ok observer sees lock"
(cd "$s/b" && ! git lfs unlock Big.uasset >/dev/null 2>&1) && echo "ok observer cannot unlock"
(cd "$s/b" && echo more >> Small.uasset && git commit -qam two && ! git push -q origin main >/dev/null 2>&1) && echo "ok observer cannot push"
stop; start me@x
git lfs unlock Big.uasset >/dev/null && echo "ok unlock"
stop; start stranger@x
rm -rf "$s/c"; ! git clone -q "$s/remote.git" "$s/c" >/dev/null 2>&1 && echo "ok stranger refused"
[[ ! -e "$s/asked" ]] && echo "ok never asked for a password" || { echo "FAIL a password was asked for"; exit 1; }
echo "all passed"; stop; rm -rf "${s:?}"
