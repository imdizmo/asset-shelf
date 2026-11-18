#!/usr/bin/env bash
# The shelf command's check: a made-up library with one loose-file pack; search, show, pull (raw), the provenance
# line and licence copy, no overwriting, and refusing a format the asset does not have.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
s="${HOME}/.cache/asset-shelf/scratch/shelf-check"
rm -rf "${s:?}"; mkdir -p "$s/home/.config/asset-shelf" "$s/lib/index/Crates_abc123/provenance" "$s/lib/packs/Crates_abc123/raw/fbx/crates/textures" "$s/out"
cat > "$s/lib/index/Crates_abc123/index.json" <<'J'
{"source":"raw","assets":[{"path":"fbx/crates/crate_big.fbx","package":"fbx/crates/crate_big.fbx","name":"crate_big","class":"StaticMesh","tags":{"Triangles":"12"}}]}
J
echo '{"title":"Wooden Crates","licence":"CC0"}' > "$s/lib/index/Crates_abc123/pack.json"
echo "terms of the licence" > "$s/lib/index/Crates_abc123/provenance/licence-2026-11-18.txt"
sum=$(sha256sum "$s/lib/index/Crates_abc123/provenance/licence-2026-11-18.txt" | cut -c1-64)
cat > "$s/lib/index/Crates_abc123/provenance.json" <<J
{"version":1,"source":{"site":"Example","url":"https://example.com/crates","creator":"Someone"},"licence":{"name":"CC0","copies":[{"file":"provenance/licence-2026-11-18.txt","from":"test","saved":"2026-11-18","sha256":"$sum","kind":"licence"}],"credit":"Crates by Someone"},"acquired":{"how":"free download"},"receipts":[]}
J
echo mesh > "$s/lib/packs/Crates_abc123/raw/fbx/crates/crate_big.fbx"; echo tex > "$s/lib/packs/Crates_abc123/raw/fbx/crates/textures/wood.png"
echo "{\"Packs\":\"$s/lib/packs\",\"Index\":\"$s/lib/index\",\"Team\":\"\"}" > "$s/home/.config/asset-shelf/settings.json"
dotnet build "$root/cli/Shelf" -c Release -v q -nologo >/dev/null
shelf() { HOME="$s/home" "$root/cli/Shelf/bin/Release/net10.0/shelf" "$@"; }
ok() { echo "ok $1"; }
id=$(shelf search wooden crate --json | python3 -I -c 'import json,sys; d=json.load(sys.stdin); assert len(d)==1, d; print(d[0]["id"])') && ok search
[[ $(shelf search nothinglikethis --json) == "[]" ]] && ok "search finds nothing"
shelf show "$id" --json | grep -q '"credit": "Crates by Someone"' && ok show
shelf pull "$id" --to "$s/out" --format raw >/dev/null
[[ -f "$s/out/Crates_abc123/fbx/crates/crate_big.fbx" && -f "$s/out/Crates_abc123/fbx/crates/textures/wood.png" ]] && ok "pull copies the file and its textures"
grep -q '"name":"crate_big"' "$s/out/AssetShelf.provenance.jsonl" && ok "provenance line"
grep -q "terms of the licence" "$s/out/licences/Crates_abc123.txt" && ok "licence copy"
echo mine > "$s/out/Crates_abc123/fbx/crates/crate_big.fbx"; shelf pull "$id" --to "$s/out" --format raw >/dev/null
[[ $(cat "$s/out/Crates_abc123/fbx/crates/crate_big.fbx") == mine ]] && ok "never overwrites"
! shelf pull "$id" --to "$s/out" --format unreal 2>/dev/null && ok "refuses a format the asset lacks"
echo "all passed"; rm -rf "${s:?}"
