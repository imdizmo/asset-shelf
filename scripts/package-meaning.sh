#!/usr/bin/env bash
# Builds the shelf-meaning add-on on its own: scripts/package-meaning.sh <rid: linux-x64|win-x64> <version> [out dir]
# It goes beside shelf (or on the PATH). The libraries it carries are shipped with their licence texts in licences/.
set -euo pipefail
rid="${1:?rid}"; version="${2:?version}"; out="${3:-dist}"
root="$(cd "$(dirname "$0")/.." && pwd)"
if ! [[ "${version#v}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "version must look like v1.2.3 (got $version)" >&2; exit 2
fi
name="shelf-meaning-${version}-${rid}"
stage="${out}/${name}"
rm -rf "${stage:?}"; mkdir -p "$stage/licences"
dotnet publish "$root/addons/ShelfMeaning/ShelfMeaning.csproj" -c Release -r "$rid" --self-contained -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:Version="${version#v}" -o "$stage"
# ONNX Runtime's package drops Windows files into every build, and Windows builds get link libraries and symbols
if [[ "$rid" == win-* ]]; then rm -f "$stage"/*.lib "$stage"/*.pdb; else rm -f "$stage"/*.dll; fi
cp "$root/addons/ShelfMeaning/NOTICE.md" "$root/LICENSE" "$stage/"
nuget="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
skia_native=skiasharp.nativeassets.linux; [[ "$rid" == win-* ]] && skia_native=skiasharp.nativeassets.win32
while read -r pkg ver file as; do
    cp "$nuget/$pkg/$ver/$file" "$stage/licences/$as"
done <<LIST
microsoft.ml.onnxruntime 1.32.0 LICENSE onnxruntime-LICENSE.txt
microsoft.ml.onnxruntime 1.32.0 ThirdPartyNotices.txt onnxruntime-ThirdPartyNotices.txt
microsoft.ml.tokenizers 3.0.0 LICENSE tokenizers-LICENSE.txt
microsoft.ml.tokenizers 3.0.0 THIRD-PARTY-NOTICES.TXT tokenizers-THIRD-PARTY-NOTICES.txt
skiasharp 3.119.4 LICENSE.txt skiasharp-LICENSE.txt
$skia_native 3.119.4 THIRD-PARTY-NOTICES.txt skia-THIRD-PARTY-NOTICES.txt
LIST
cd "$out"
if [[ "$rid" == win-* ]]; then
    rm -f "$name.zip"; (command -v zip >/dev/null && zip -qr "$name.zip" "$name") || python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.', sys.argv[1])" "$name"
    echo "$out/$name.zip"
else
    tar -czf "$name.tar.gz" "$name"; echo "$out/$name.tar.gz"
fi
