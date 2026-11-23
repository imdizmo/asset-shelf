#!/usr/bin/env bash
# The server's check: runs it on loopback with a made-up library and team list, and tries each route with curl.
# SHELF_BIN=<a ShelfServer program> checks that one instead (a released, trimmed build behaves differently).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
s="${HOME}/.cache/asset-shelf/scratch/server-test"
rm -rf "${s:?}"; mkdir -p "$s/lib/index/P1/thumbs" "$s/lib/packs/P1/unreal/Content" "$s/store"
echo '{"title":"P1"}' > "$s/lib/index/P1/pack.json"; echo thumb > "$s/lib/index/P1/thumbs/a.png"
echo data > "$s/lib/packs/P1/unreal/Content/A.uasset"; ln -s /etc/passwd "$s/lib/packs/P1/link"
echo '{"version":1,"members":[{"login":"me@x","name":"Me","role":"member"},{"login":"obs@x","name":"O","role":"observer"}]}' > "$s/team.json"
dotnet build -c Release "$root/server/ShelfServer" -v q -nologo >/dev/null
port=18781; u="http://127.0.0.1:$port"
start() { SHELF_DEV_LOGIN="$1" "${SHELF_BIN:-$root/server/ShelfServer/bin/Release/net10.0/ShelfServer}" --listen 127.0.0.1:$port --library "$s/lib" --store "$s/store" --team "$s/team.json" --max-upload 100000 >>"$s/log" 2>&1 & spid=$!; for _ in $(seq 50); do curl -s "$u/team/whoami" >/dev/null && return; sleep 0.1; done; }
trap 'kill $spid 2>/dev/null' EXIT
code() { curl -s -o "$s/out" -w '%{http_code}' "$@"; }
want() { [[ "$1" == "$2" ]] || { echo "FAIL $3: got $1 want $2: $(cat "$s/out")"; exit 1; }; echo "ok $3"; }
start me@x
want "$(code $u/team/whoami)" 200 whoami; grep -q '"role":"member"' "$s/out"
want "$(code -H "Host: anything.example" $u/healthz)" 200 healthz; [[ "$(cat "$s/out")" == ok ]]
grep -q "SHELF_LISTENING $port" "$s/log" && echo "ok ready-line"
want "$(code -H "Host: rebound.example:$port" -X DELETE -H 'X-Jatify: 1' $u/team/projects/game)" 421 other-host-refused
want "$(code -H "Host: localhost:$port" $u/team/whoami)" 200 localhost-host
want "$(code $u/team/projects)" 200 projects-none; grep -q '"projects":\[\]' "$s/out"
cat > "$s/projects.json" <<'J'
{"version":1,"projects":[
 {"id":"game","name":"The Game","description":"d","git":"https://example.com/o/game.git","lfs":"game","setup":"tools/setup.sh","branch":"main","folder":"Game","memoryMax":"16G","secret":"x"},
 {"id":"opt","git":"git@example.com:o/opt.git","setup":"setup.sh","branch":"-x","folder":"../up","memoryMax":"lots"},
 {"id":"bad id","git":"https://example.com/x.git","setup":"s.sh"},
 {"id":"argv","git":"https://example.com/x.git","setup":["powershell","-c","x"]},
 {"id":"up","git":"https://example.com/x.git","setup":"tools/../../evil.sh"},
 {"id":"abs","git":"https://example.com/x.git","setup":"/bin/sh"},
 {"id":"dash","git":"-uhttps://example.com/x.git","setup":"s.sh"}]}
J
want "$(code $u/team/projects)" 200 projects
python3 -I - "$s/out" "$port" <<'P'
import json,sys
d=json.load(open(sys.argv[1])); p={x["id"]:x for x in d["projects"]}
assert d["schema"]==1 and sorted(p)==["game","opt"], p
g=p["game"]; assert g["lfs"]==f"http://127.0.0.1:{sys.argv[2]}/lfs/game" and g["setup"]=="tools/setup.sh" and g["folder"]=="Game" and g["memoryMax"]=="16G" and "secret" not in g
o=p["opt"]; assert o["name"]=="opt" and o["lfs"] is None and not {"branch","folder","memoryMax"} & set(o), o
P
echo "ok projects-checked"
want "$(code $u/library)" 200 library; grep -q '"pack.json"' "$s/out"
want "$(code $u/library/P1/index/thumbs/a.png)" 200 thumb
want "$(code $u/library/P1/files)" 200 listing; grep -q 'A.uasset' "$s/out"; ! grep -q '"link"' "$s/out"
want "$(code $u/library/P1/files/unreal/Content/A.uasset)" 200 file
want "$(code $u/library/P1/files/link)" 404 symlink
want "$(code --path-as-is $u/library/P1/files/../../index/P1/pack.json)" 404 dotdot
want "$(code $u/library/P1/files/%2e%2e/x)" 404 encoded-dotdot
python3 -I -c 'import zipfile,sys; zipfile.ZipFile(sys.argv[1],"w").writestr("m/map.json","{}")' "$s/z.zip"; head -c 200000 /dev/zero > "$s/big.zip"
head -c 40 "$s/z.zip" > "$s/cut.zip"
want "$(code -X PUT --data-binary @"$s/z.zip" "$u/maps/abc?name=Town")" 201 publish
want "$(code -X PUT --data-binary @"$s/z.zip" -H 'If-Match: 1' "$u/maps/abc?name=Town2")" 200 version2; grep -q '"version":2' "$s/out"
want "$(code -X PUT --data-binary @"$s/z.zip" -H 'If-Match: 1' "$u/maps/abc")" 409 stale
want "$(code -X PUT --data-binary @"$s/big.zip" "$u/maps/abc")" 413 too-big
want "$(code -X PUT --data-binary 'nope' "$u/maps/abc")" 400 not-zip
want "$(code -X PUT --data-binary @"$s/cut.zip" "$u/maps/abc")" 400 cut-zip
want "$(code -X PUT --data-binary @"$s/z.zip" "$u/maps/a.b")" 400 bad-id
want "$(code $u/maps)" 200 list; grep -q '"name":"Town2"' "$s/out"
want "$(code $u/maps/abc/pack)" 200 download; cmp -s "$s/out" "$s/z.zip"
want "$(code "$u/maps/abc/pack?version=1")" 200 download-v1
etag=$(curl -sI $u/maps/abc/pack | tr -d '\r' | sed -n 's/^ETag: //Ip')
want "$(code -H "If-None-Match: $etag" $u/maps/abc/pack)" 304 etag
ls -a "$s/store/maps/abc" | grep -q upload && { echo "FAIL leftover upload"; exit 1; }
want "$(code $u/maps/abc)" 200 versions; grep -q '"version":1' "$s/out"
want "$(code -X POST -H Content-Type:application/zip --data-binary @"$s/z.zip" "$u/team/crashes?tool=game-tool&version=1.2.3&os=windows")" 201 crash-sent; grep -q '"id":"game-tool/' "$s/out"
python3 -I - "$s" <<'PY'
import gzip, json, sys
s = sys.argv[1]
def log(name, player, started, extra=b"", game="zone"):
    head = {"playlog": 1, "game": game, "build": "abc", "map": {"name": "Tiers", "team": "t1", "version": 2}, "player": player, "started": started}
    lines = [json.dumps(head)] + [json.dumps(e) for e in [{"t": 1.0, "e": "pos", "p": [1, 2, 3], "yaw": 90}, {"t": 5.5, "e": "death", "p": [1, 2, 3], "by": "wolf"},
             {"t": 6.0, "e": "respawn", "p": [0, 0, 0]}, {"t": 9.0, "e": "quest", "id": "q1", "step": "done"}, {"t": 12.3, "e": "end", "why": "quit"}]]
    with gzip.open(f"{s}/{name}", "wb") as f:
        f.write(("\n".join(lines) + "\n").encode() + extra)
log("a.playlog.gz", "3fa9c1d2", "2026-11-18T20:14:03Z")
log("b.playlog.gz", "0badcafe", "2026-11-18T21:00:00Z", b'{"t":13.0,"e":"pos"' )   # a torn last line
log("other.playlog.gz", "3fa9c1d2", "2026-11-18T20:14:03Z", game="othergame")
log("badplayer.playlog.gz", "Bob", "2026-11-18T20:14:03Z")
with gzip.open(f"{s}/bomb.playlog.gz", "wb") as f:   # unpacks past 200 MB
    f.write((json.dumps({"playlog": 1, "game": "zone", "player": "deadbeef", "started": "2026-11-18T22:00:00Z"}) + "\n").encode())
    line = b'{"t":1,"e":"pos"}\n' * 100000
    for _ in range(int(210e6 / len(line)) + 1): f.write(line)
PY
P="-H Content-Type:application/gzip"
want "$(code -X POST $P --data-binary @"$s/a.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 201 playlog-sent
plid=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "$s/out")
python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])); assert d["id"].startswith("zone/") and "-3fa9c1d2-" in d["id"] and d["counts"]["death"]==1 and d["counts"]["quest"]==1 and d["length"]==12.3 and d["ended"]=="quit" and d["map"]["version"]==2 and "sentBy" not in d, d' "$s/out" && echo "ok playlog-facts"
want "$(code -X POST $P --data-binary @"$s/a.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 200 playlog-resent-kept-once; grep -q "\"$plid\"" "$s/out"
want "$(code -X POST -H 'Content-Type: text/plain' --data-binary @"$s/a.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 415 playlog-plain-text-refused
want "$(code -X POST --data-binary @"$s/z.zip" "$u/team/crashes?tool=t&version=1&os=linux")" 415 crash-form-post-refused
want "$(code -X POST $P --data-binary @"$s/b.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 201 playlog-torn-line
want "$(code -X POST $P --data-binary @"$s/other.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 400 playlog-wrong-game
want "$(code -X POST $P --data-binary @"$s/badplayer.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 400 playlog-bad-player
want "$(code -X POST $P --data-binary 'plain text' "$u/team/playlogs?game=zone&build=abc")" 400 playlog-not-gzip
want "$(code -X POST $P --data-binary @"$s/a.playlog.gz" "$u/team/playlogs?game=../x&build=abc")" 400 playlog-bad-game
want "$(code -X POST $P --data-binary @"$s/bomb.playlog.gz" "$u/team/playlogs?game=zone&build=abc")" 201 playlog-bomb; grep -q '"partial":true' "$s/out"
want "$(code "$u/team/playlogs?game=zone")" 200 playlog-list; ! grep -q sentBy "$s/out"; grep -q 0badcafe "$s/out"
want "$(code "$u/team/playlogs/$plid")" 200 playlog-download; cmp -s "$s/out" "$s/a.playlog.gz"
want "$(code -X DELETE "$u/team/playlogs/$plid")" 400 playlog-delete-needs-header
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/playlogs/$plid")" 200 playlog-sender-deletes
bid=$(python3 -I -c 'import json,sys; print([r["id"] for r in json.load(open(sys.argv[1])) if r["player"]=="0badcafe"][0])' <(curl -s "$u/team/playlogs?game=zone")); echo "$bid" > "$s/bid"
want "$(code $u/team/crashes)" 403 crash-list-member
want "$(code -X DELETE "$u/team/crashes/game-tool/x.zip")" 403 crash-delete-member
want "$(code -X PUT -H 'X-Jatify: 1' -H 'Content-Type: application/json' -d '{"git":"https://example.com/n.git","setup":"s.sh"}' "$u/team/projects/m")" 403 project-member
want "$(code $u/team/server)" 200 server-status-member
want "$(code -X POST -H Content-Type:application/zip --data-binary @"$s/z.zip" "$u/team/crashes?tool=Bad/Tool&version=1&os=linux")" 400 crash-bad-tool
want "$(code -X POST -H Content-Type:application/zip --data-binary 'nope' "$u/team/crashes?tool=t&version=1&os=linux")" 400 crash-not-zip
head -c 21000000 /dev/zero > "$s/huge.zip"
want "$(code -X POST -H Content-Type:application/zip --data-binary @"$s/huge.zip" "$u/team/crashes?tool=t&version=1&os=linux")" 413 crash-too-big
python3 -I - "$s" <<'PY'
import zipfile, sys
s = sys.argv[1]; png = b"\x89PNG\r\n\x1a\n" + b"x" * 100; jpg = b"\xff\xd8\xff\xe0" + b"x" * 100
def z(name, files):
    with zipfile.ZipFile(f"{s}/{name}", "w") as f:
        for n, d in files.items(): f.writestr(n, d)
z("sg.zip", {"suggestion.txt": "The search box could remember my last words.\n", "shot-1.png": png, "shot 2.jpg": jpg})
z("sg-ok.zip", {"suggestion.txt": "Two shots.", "a.png": png, "b.jpeg": jpg})
z("sg-none.zip", {"a.png": png})
z("sg-blank.zip", {"suggestion.txt": "  \n "})
z("sg-long.zip", {"suggestion.txt": "x" * 20001})
z("sg-fake.zip", {"suggestion.txt": "hi", "a.png": b"MZ not a picture"})
z("sg-exe.zip", {"suggestion.txt": "hi", "run.exe": b"MZ"})
z("sg-dir.zip", {"suggestion.txt": "hi", "../a.png": png})
z("sg-many.zip", {"suggestion.txt": "hi", **{f"{i}.png": png for i in range(11)}})
with open(f"{s}/sg-latin1.zip", "wb"): pass
z("sg-latin1.zip", {"suggestion.txt": "caf\xe9".encode("latin-1")})
PY
SG="-X POST -H Content-Type:application/zip"
want "$(code $SG --data-binary @"$s/sg.zip" "$u/team/suggestions?tool=asset-shelf&version=0.4.0&os=linux")" 400 suggestion-bad-name
want "$(code $SG --data-binary @"$s/sg-ok.zip" "$u/team/suggestions?tool=asset-shelf&version=0.4.0&os=linux")" 201 suggestion-sent; grep -q '"id":"asset-shelf/' "$s/out"
want "$(code $SG --data-binary @"$s/sg-ok.zip" "$u/team/suggestions?tool=questlog&version=0.3.0&os=web")" 201 suggestion-from-web
for bad in none blank long fake exe dir many latin1; do want "$(code $SG --data-binary @"$s/sg-$bad.zip" "$u/team/suggestions?tool=t&version=1&os=linux")" 400 "suggestion-$bad-refused"; done
want "$(code -X POST --data-binary @"$s/sg-ok.zip" "$u/team/suggestions?tool=t&version=1&os=linux")" 415 suggestion-form-post-refused
want "$(code $SG --data-binary @"$s/huge.zip" "$u/team/suggestions?tool=t&version=1&os=linux")" 413 suggestion-too-big
want "$(code $u/team/suggestions)" 403 suggestion-list-member
pre() { curl -s -o /dev/null -D "$s/hdr" -w '%{http_code}' -X OPTIONS -H "Origin: $1" -H 'Access-Control-Request-Method: POST' "$u$2"; }
want "$(pre "http://127.0.0.1:8780" /team/suggestions)" 204 suggestion-preflight-same-machine; grep -qi '^Access-Control-Allow-Origin: http://127.0.0.1:8780' "$s/hdr"
pre "http://evil.example:8780" /team/suggestions >/dev/null; ! grep -qi '^Access-Control' "$s/hdr" && echo "ok suggestion-preflight-other-site-refused"
curl -s -o /dev/null -D "$s/hdr" -H 'Origin: http://127.0.0.1:8780' $u/team/whoami; grep -qi '^Access-Control-Allow-Origin: http://127.0.0.1:8780' "$s/hdr" && echo "ok whoami-readable-by-same-machine-page"
pre "http://127.0.0.1:9999" /team/suggestions >/dev/null; ! grep -qi '^Access-Control' "$s/hdr" && echo "ok preflight-other-port-refused"
for r in /library /library/P1/index/thumbs/a.png /maps /props; do curl -s -o /dev/null -D "$s/hdr" -H 'Origin: http://127.0.0.1:8780' "$u$r"; grep -qi '^Access-Control-Allow-Origin: http://127.0.0.1:8780' "$s/hdr" || { echo "FAIL page cannot read $r"; exit 1; }; done; echo "ok page-reads-library-index"
for r in /library/P1/files /library/P1/files/unreal/Content/A.uasset /maps/abc/pack /team/projects /team/server; do curl -s -o /dev/null -D "$s/hdr" -H 'Origin: http://127.0.0.1:8780' "$u$r"; ! grep -qi '^Access-Control' "$s/hdr" || { echo "FAIL page can read $r"; exit 1; }; done; echo "ok page-cannot-read-files-or-zips"
curl -s -o /dev/null -D "$s/hdr" -H 'Origin: http://evil.example' $u/team/whoami; ! grep -qi '^Access-Control' "$s/hdr" && echo "ok whoami-not-readable-by-other-site"
pre "http://127.0.0.1:8780" /team/crashes >/dev/null; ! grep -qi '^Access-Control' "$s/hdr" && echo "ok preflight-only-for-suggestions"
want "$(curl -s -o "$s/out" -D "$s/hdr" -w '%{http_code}' $SG -H 'Origin: http://127.0.0.1:8780' --data-binary @"$s/sg-ok.zip" "$u/team/suggestions?tool=questlog&version=0.3.0&os=web")" 201 suggestion-from-page; grep -qi '^Access-Control-Allow-Origin: http://127.0.0.1:8780' "$s/hdr" && echo "ok suggestion-page-reads-answer"
curl -s -o /dev/null -D "$s/hdr" -H 'Origin: http://localhost:8780' $u/team/whoami; ! grep -qi '^Access-Control' "$s/hdr" || { echo "FAIL a one-word name counts as Questlog's page"; exit 1; }; echo "ok whoami-not-readable-by-one-word-name"
[[ $(ls "$s/store/suggestions/t" 2>/dev/null | wc -l) == 0 ]] && echo "ok suggestion-refused-not-kept"
python3 -I -c 'import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],"w"); z.writestr("p/picture.png","PNGDATA"); z.writestr("p/prop.propgen","{}")' "$s/p.zip"
want "$(code -X PUT --data-binary @"$s/p.zip" "$u/props/p1?name=Crate")" 201 prop
want "$(code $u/props/p1/picture)" 200 picture; [[ "$(cat "$s/out")" == PNGDATA ]]
want "$(code $u/maps/abc/picture)" 404 no-picture
want "$(code -X DELETE $u/maps/abc)" 403 member-cannot-delete
kill $spid; wait $spid 2>/dev/null || true
echo '{"version":1,"members":[{"login":"boss@x","name":"Boss","role":"admin"}]}' > "$s/team2.json"; cp "$s/team.json" "$s/team1.json"; cp "$s/team2.json" "$s/team.json"
start boss@x
want "$(code $u/team/crashes)" 200 crash-list-admin; grep -q 'game-tool/' "$s/out"
cid=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))[0]["id"])' "$s/out")
want "$(code "$u/team/crashes/$cid")" 200 crash-download; cmp -s "$s/out" "$s/z.zip"
want "$(code "$u/team/crashes/game-tool/..%2f..%2fteam.json")" 404 crash-traversal
[[ "$cid" =~ ^game-tool/[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\.zip$ ]] && echo "ok crash-name-has-no-login" || { echo "FAIL crash id $cid"; exit 1; }
grep -q '"login":"me@x"' "$s/store/crashes/$cid.json" && echo "ok crash-login-in-json"
want "$(code -X DELETE "$u/team/crashes/$cid")" 400 crash-delete-needs-header
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/crashes/$cid")" 200 crash-delete
[[ ! -e "$s/store/crashes/$cid" && ! -e "$s/store/crashes/$cid.json" ]] && echo "ok crash-deleted-both"
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/crashes/$cid")" 404 crash-delete-again
want "$(code $u/team/suggestions)" 200 suggestion-list-admin
curl -s -o /dev/null -D "$s/hdr" -H 'Origin: http://127.0.0.1:8780' $u/team/suggestions; ! grep -qi '^Access-Control' "$s/hdr" && echo "ok suggestion-list-not-readable-by-page"
python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])); r=[x for x in d if x["tool"]=="asset-shelf"][0]; assert len(d)==3 and r["text"]=="Two shots." and r["screenshots"]==["a.png","b.jpeg"] and r["login"]=="me@x" and r["version"]=="0.4.0", d; print(r["id"])' "$s/out" > "$s/sgid" && echo "ok suggestion-text-in-list"
sgid=$(cat "$s/sgid")
want "$(code "$u/team/suggestions/$sgid")" 200 suggestion-download; cmp -s "$s/out" "$s/sg-ok.zip"
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/suggestions/$sgid")" 200 suggestion-delete
want "$(code "$u/team/suggestions/$sgid")" 404 suggestion-gone
want "$(code $u/team/server)" 200 server-status; grep -q '"service":"shelf"' "$s/out"; grep -q '"diskFree":' "$s/out"
J='-H X-Jatify:1 -H Content-Type:application/json'
want "$(code -X PUT $J -d '{"name":"New","git":"https://example.com/n.git","setup":"setup.py","lfs":"new","memoryMax":"8G"}' "$u/team/projects/newp")" 201 project-add; grep -q '"lfs":"http://127.0.0.1:'$port'/lfs/new"' "$s/out"
want "$(code -X PUT $J -d '{"name":"New 2","git":"https://example.com/n.git","setup":"setup.py"}' "$u/team/projects/newp")" 200 project-change
want "$(code -X PUT $J -d '{"git":"https://example.com/n.git","setup":"../s.sh"}' "$u/team/projects/bad")" 400 project-bad-setup; grep -q 'setup must be' "$s/out"
want "$(code -X PUT $J -d '{"git":"https://example.com/n.git","setup":"setup.py","memoryMax":"lots"}' "$u/team/projects/bad")" 400 project-bad-optional
want "$(code -X PUT $J -d '{"git":"https://example.com/n.git","setup":"tools/setup.sh"}' "$u/team/projects/sh")" 400 project-setup-must-be-python
want "$(code -X PUT -H 'Content-Type: application/json' -d '{"git":"https://example.com/n.git","setup":"s.sh"}' "$u/team/projects/x")" 400 project-needs-header
want "$(code -X PUT -H 'X-Jatify: 1' -H 'Content-Type: text/plain' -d '{"git":"https://example.com/n.git","setup":"s.sh"}' "$u/team/projects/x")" 400 project-needs-json
want "$(code $u/team/projects)" 200 project-list; grep -q '"name":"New 2"' "$s/out"; grep -q '"id":"game"' "$s/out"
grep -q '"lfs": "game"' "$s/projects.json" && echo "ok project-file-keeps-lfs-name"
grep -q '"bad id"' "$s/projects.json" && echo "ok project-file-keeps-hand-entries" || { echo "FAIL a hand-written entry was dropped"; exit 1; }
head -c 3000000 /dev/zero | tr '\0' ' ' > "$s/big.json"
want "$(code -X PUT -H 'X-Jatify: 1' -H 'Content-Type: application/json' -H 'Transfer-Encoding: chunked' --data-binary @"$s/big.json" "$u/team/projects/newp")" 400 project-chunked-too-big
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/projects/newp")" 200 project-delete
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/projects/newp")" 404 project-delete-again
for i in $(seq 501); do curl -s -o /dev/null -X POST -H Content-Type:application/zip --data-binary @"$s/z.zip" "$u/team/crashes?tool=spam&version=1&os=linux"; done
[[ $(ls "$s/store/crashes/spam" "$s/store/crashes/game-tool" | grep -c '\.zip$') == 500 ]] && echo "ok crash-store-bounded" || { echo "FAIL crash store not bounded"; exit 1; }
want "$(code -X DELETE "$u/maps/abc?version=1")" 200 delete-version; grep -q '"versionsLeft":1' "$s/out"
want "$(code "$u/maps/abc/pack?version=1")" 404 version-gone
want "$(code -X DELETE "$u/maps/abc?version=9")" 404 delete-missing
want "$(code -X DELETE $u/maps/abc)" 200 delete-all
want "$(code $u/maps)" 200 list-empty; [[ "$(cat "$s/out")" == "[]" ]]
kill $spid; wait $spid 2>/dev/null || true
cp "$s/team1.json" "$s/team.json"
start obs@x
want "$(code -X PUT --data-binary @"$s/z.zip" "$u/props/p1")" 403 observer
want "$(code $u/props)" 200 observer-read
want "$(code "$u/team/playlogs")" 200 playlog-observer-lists
want "$(code -X DELETE -H 'X-Jatify: 1' "$u/team/playlogs/$(cat "$s/bid")")" 403 playlog-others-cannot-delete
kill $spid; wait $spid 2>/dev/null || true
start stranger@x
want "$(code $u/team/whoami)" 200 stranger-whoami; grep -q '"role":null' "$s/out"
want "$(code $u/library)" 403 stranger
want "$(code $u/team/projects)" 403 stranger-projects
echo "all passed"; rm -rf "${s:?}"
