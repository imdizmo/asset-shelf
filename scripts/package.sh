#!/usr/bin/env bash
# Builds a release package: scripts/package.sh <rid: linux-x64|win-x64> <version> [out dir]
# The app and ShelfIndex are published self-contained (no .NET needed to run them); the tools, the Blender add-on,
# the Unreal plugin and the docs go beside them, so the app finds tools/ next to itself.
set -euo pipefail
rid="${1:?rid}"; version="${2:?version}"; out="${3:-dist}"
root="$(cd "$(dirname "$0")/.." && pwd)"
name="AssetShelf-${version}-${rid}"
stage="${out}/${name}"
rm -rf "${stage:?}"; mkdir -p "$stage/tools/ShelfIndex"
publish=(-c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:Version="${version#v}")
dotnet publish "$root/app/AssetShelf.csproj" "${publish[@]}" -o "$stage"
dotnet publish "$root/tools/ShelfIndex/ShelfIndex.csproj" "${publish[@]}" -o "$stage/tools/ShelfIndex"
cp "$root"/tools/*.py "$root/tools/requirements.txt" "$stage/tools/"
cp "$root/tools/ShelfIndex/Program.cs" "$root/tools/ShelfIndex/ShelfIndex.csproj" "$stage/tools/ShelfIndex/"
mkdir -p "$stage/blender" "$stage/unreal"
cp "$root"/blender/*.py "$stage/blender/"
cp -r "$root/unreal/AssetShelf" "$stage/unreal/"
rm -rf "$stage/unreal/AssetShelf/Binaries" "$stage/unreal/AssetShelf/Intermediate"
cp "$root/README.md" "$root/LICENSE" "$root/NOTICE.md" "$stage/"
cd "$out"
if [[ "$rid" == win-* ]]; then
    rm -f "$name.zip"; (command -v zip >/dev/null && zip -qr "$name.zip" "$name") || python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.', sys.argv[1])" "$name"
    echo "$out/$name.zip"
else
    tar -czf "$name.tar.gz" "$name"
    echo "$out/$name.tar.gz"
fi
