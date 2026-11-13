# Asset Shelf

A desktop browser for your library of 3D asset packs: every asset of every pack in one grid with its picture, filters by
kind, tag, pack and licence, and buttons to send what you picked to **Unreal**, **Blender** or **Unity**, with
everything it needs.

It reads Unreal packs (from Fab or the old Unreal Marketplace) and loose files (FBX, glTF, OBJ, `.blend`, Unity
packages, from itch.io or anywhere). Unreal packs are indexed **without Unreal**: the asset list, details,
dependencies and the Content Browser's own pictures are read straight from the `.uasset` files.

Status: 0.1, used daily on Linux; Windows and macOS are untested.

## What it does

- **One grid for everything.** Kinds as tabs (meshes, characters, animations, effects, sounds, blueprints, maps,
  materials, textures), tags, packs and licences as filters, search, collections, favourites, notes and saved filters.
- **What comes with an asset.** Each asset lists the packages it needs (followed through the pack) and what works with
  it (animations on the same skeleton, blueprints that use it). Duplicates shipped in several packs are linked.
- **Packs view.** Every pack with its store pictures, description and licence, and a preview of its assets.
- **Send to Unreal.** Copies the asset and everything it needs into the project's `Content` at the same paths, after a
  preview of what will be copied. Never overwrites. Flags packs saved with a newer Unreal than the project, packs with
  C++ code or plugins, and anything that could run code (the safety scan).
- **Send to / Open in Blender.** Imports into the open Blender (through the add-on) or a new Blender window, with the
  materials wired: base colour, normal (DirectX for Unreal assets), roughness, metallic, packed ORM maps, emission.
- **Send to Unity.** The model, the textures its materials use, and Unity materials for the project's pipeline (URP,
  HDRP or Built-in), with the import settings written so normal maps and data maps come in right.
- **Spin in 3D.** A turntable rendered once by headless Blender, turned by dragging.
- **Library sync.** Fab (sign in with your Epic account) and itch.io (an API key): what is new or updated, downloaded
  and checked file by file, then indexed. A preview first; nothing is downloaded until you say so.
- **Debug log** (View > Debug log, Ctrl+Shift+L): every step with its timing.

## Download

Releases have a package for Linux (x64, `.tar.gz`) and Windows (x64, `.zip`): unpack it anywhere and run `AssetShelf`
(`AssetShelf.exe`). .NET is included. Blender, Python and Unreal are found on the `PATH` or set in `settings.json`
(`Blender`). Windows builds are made by the same CI but not yet tested on Windows; reports are welcome.

To make a release: push a tag such as `v0.2.0` (`.github/workflows/release.yml` builds both packages with
`scripts/package.sh` and publishes them).

## Requirements

| For | Needs |
|---|---|
| The app | nothing for a release package; the .NET 10 SDK to build from source |
| Indexing Unreal packs | nothing more (ShelfIndex, .NET) |
| Exporting Unreal assets to Blender/Unity | Unreal Engine 5 (`~/UnrealEngine` or set `--engine`) |
| Loose files, materials, turntables, Unity prep | Blender 5.1 or later on the `PATH` |
| Fab and itch.io sync | Python 3.11+, `tools/requirements.txt` (legendary-gl, requests), `secret-tool` (libsecret) |
| Unity | a Unity project (tested with Unity 6; glTF models are not handled yet) |

## Setup

```sh
dotnet build app -c Release
dotnet build tools/ShelfIndex -c Release
python3 -m venv tools/.venv && tools/.venv/bin/pip install -r tools/requirements.txt
```

The Blender add-on is `blender/asset_shelf_bridge.py` (Edit > Preferences > Add-ons > Install from Disk). The Unreal
plugin in `unreal/AssetShelf` is only needed for the older Unreal-based indexing path (`index_library.py --unreal`)
and for rendered pictures of effects.

Settings live in `~/.config/asset-shelf/settings.json` (most are also in the File menu):

```json
{
  "Packs": "/path/to/library/packs",
  "Index": "/path/to/library/index",
  "UnrealProject": "",
  "UnityProject": "",
  "UnityFolder": "Assets/AssetShelf",
  "UnityMirrorPack": false
}
```

Run with `dotnet run --project app -c Release` (add `--debug` to open the log on start).

## Library layout

```
<packs>/<pack>/unreal/Content/...        an Unreal pack (or a whole sample project)
<packs>/<pack>/unreal.manifest           the Epic manifest it was downloaded from (Fab sync, verify_pack.py)
<packs>/<pack>/raw/<format>/...          loose model files and archives (unpacked beside themselves)
<index>/<pack>/index.json                one entry per asset: class, details, dependencies, who uses it, picture
<index>/<pack>/thumbs/                   pictures
<index>/<pack>/pack.json                 store data: title, seller, licence, url, description, cover, gallery
<index>/<pack>/safety.json               the safety scan's findings
```

## Tools

| Script | Does |
|---|---|
| `tools/index_library.py` | indexes every pack that has no index yet (`--kind unreal|raw`, `--redo <list>`), then scans it |
| `tools/ShelfIndex` | indexes one Unreal pack without Unreal (CUE4Parse; only the files' headers are read) |
| `tools/index_raw.py` | indexes one pack of loose files with headless Blender |
| `tools/safety_scan.py` | lists what in a pack can run code (startup scripts, binaries, C++, plugins, config commands) |
| `tools/export_fbx.py` | exports one Unreal asset to FBX and its textures, in a small throwaway project |
| `tools/fab_sync.py` | Epic sign-in and the Fab library sync |
| `tools/itch_sync.py` | itch.io sign-in and library sync |
| `tools/verify_pack.py` | checks a downloaded Fab pack against its manifest |
| `tools/picture_report.py` | which assets have a picture, and from where |

Run heavy ones under a memory cap, for example `systemd-run --user --scope -p MemoryMax=6G -p MemorySwapMax=0 ...`.

## Notes

- **Licences.** Asset Shelf shows each pack's licence as the store states it; reading and following it is up to you.
  Store-bought assets may not be redistributed: keep your library out of public repositories.
- **Fab sync** uses the Epic launcher's sign-in and Fab's launcher endpoints, as legendary and Heroic do. They are not
  documented by Epic and can change. The launcher client credentials in `fab_sync.py` are the public ones those
  projects use; your own token is kept in the system keyring.
- **itch.io sync** uses itch.io's public API with your API key, also kept in the keyring.

## License

MIT, see `LICENSE`. Third-party parts and their licences are listed in `NOTICE.md`.
