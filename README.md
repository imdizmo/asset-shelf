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
- **Send to Groundwork.** Meshes become self-contained .glb files (textures embedded, capped at 2048 px; origin at the
  base, metres, Y up) delivered to Groundwork's inbox (`~/.local/share/Groundwork/inbox`, `%LOCALAPPDATA%\Groundwork\inbox`
  on Windows); the open map takes them in, and the status bar shows Groundwork's answer.
- **Sounds by what they sound like.** Measured from the audio (no AI): length, loudness, pitch, brightness, attack,
  clean loops, channels. Filter and order the Sounds tab by them and play a sound from its details.
- **Prepare a model (with Blender).** Check it for holes, inside-out faces and stray specks; repair, lighten, add
  LODs and a collision shape; export for Unreal, Godot, Groundwork or Blender, with a report. The asset is never
  changed.
- **Spin in 3D.** A turntable rendered once by headless Blender, turned by dragging.
- **Free sources.** Poly Haven (models and textures; powered by Poly Haven) and ambientCG (materials), all CC0: pick
  from their catalogs, and the assets come in with their provenance filled in. Kenney's packs come through itch.io:
  claim the free ones on kenney.itch.io, and the itch.io sync brings them in.
- **Library sync.** Fab (sign in with your Epic account) and itch.io (an API key): what is new or updated, downloaded
  and checked file by file, then indexed. A preview first; nothing is downloaded until you say so.
- **Debug log** (View > Debug log, Ctrl+Shift+L): every step with its timing.

## Download

Releases have packages for Linux (x64: an `.AppImage`, or a `.tar.gz`) and Windows (x64, `.zip`). The AppImage is one
file: make it executable and run it (an AppImage launcher such as Gear Lever adds it to the app menu). The archives
unpack anywhere; run `AssetShelf` (`AssetShelf.exe`), and on Linux `./install-desktop-entry.sh` adds it to the app menu
with its icon (run it again after moving the folder; `--remove` takes it out). .NET and Python are included. Blender, Python and
Unreal are found on the `PATH` or set in `settings.json` (`Blender`). Windows builds are made by the same CI but not yet tested on Windows; reports are welcome.

To make a release: push a tag such as `v0.2.0` (`.github/workflows/release.yml` builds the packages with
`scripts/package.sh` and publishes them).

## Requirements

| For | Needs |
|---|---|
| The app | nothing for a release package; the .NET 10 SDK to build from source |
| Indexing Unreal packs | nothing more (ShelfIndex, .NET) |
| Exporting Unreal meshes, characters and animations to Blender/Unity/Groundwork | nothing more: meshes, skinned characters with their skeleton, animations (UE 4 and UE 5, including 5.2+ sequencer data) and textures wired as each material uses them are read straight from the packages (ShelfIndex) |
| Exporting other Unreal assets (effects, blueprints, maps) | Unreal Engine 5.8 or later. On Windows the app offers to install it through Epic's launcher (installing the launcher too) when none is found; elsewhere put it in `~/UnrealEngine` or set `UnrealEngine` in the settings |
| Loose files, materials, turntables, Unity prep | Blender 5.1 or later on the `PATH` |
| Fab and itch.io sync | `secret-tool` (libsecret) on Linux; the first sync offers to set up its packages (legendary-gl, requests) in `~/.local/share/asset-shelf/venv` |
| Unity | a Unity project (tested with Unity 6; glTF models are not handled yet) |

## Setup

```sh
git submodule update --init   # third_party/CUE4Parse
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

### Sharing a library with a team

Put the library folder (holding `packs/` and `index/`) on a file server (SMB, such as a NAS) and give each team member
an account there with read-only access to it. Each member uses File > Connect to a shared library… with
`\\server\share\folder`, a user name and a password. The password is kept by the system (Windows Credential
Manager; on Linux the keyring, and the share is mounted with GNOME's gvfs-smb). A library the user cannot write to
opens in member mode: Sync and sign-in are hidden, so only the owner syncs and indexes it. The connection is kept as
`Share` and `ShareUser` in the settings.

### A team library server

On a team whose machines share a [Tailscale](https://tailscale.com) network, the library can be served by
`server/ShelfServer` instead (see [server/README.md](server/README.md)). Nobody signs in: the server asks Tailscale
who is connecting and finds them in the team list. Asset Shelf uses the server given by File > Team server…, or by
`JATIFY_TEAM` (host or host:port, port 8781 by default) when it is started by a launcher. It mirrors the index into
`~/.cache/asset-shelf/team/<host>`, fetches pictures as they are shown and pack files when an asset is sent, and opens
in member mode. The same server keeps maps and props that teammates publish from other tools.

### From the command line

`shelf` (beside the app in a release; `dotnet run --project cli/Shelf --` in a checkout) searches the same library,
shows a pack or an asset, and pulls an asset into a project, with a line in `AssetShelf.provenance.jsonl` and the
pack's licence text beside it:

```sh
shelf search tavern table --kind Mesh
shelf show <asset id>
shelf pull <asset id> --to ~/my-game/assets            # a model file (glTF) with its textures
shelf pull <asset id> --to ~/MyUnrealProject --format unreal
```

**Search by meaning** ("a person wielding a sword", whatever the files are called) is an add-on, `shelf-meaning`
(`addons/ShelfMeaning`, its own download on the release page, `shelf-meaning-<version>-<platform>`),
installed on its own: it runs Google's SigLIP 2 on this computer (downloaded once, about
1.5 GB) over each asset's picture. Asset Shelf and `shelf` carry no model and no AI library; with the add-on beside
`shelf` or on the PATH, `shelf search --meaning "…"` and `--like <asset id>` work, otherwise they say so.
`shelf-meaning embed --pack <id>` reads a pack's pictures once (vectors kept beside its index).

`shelf install-skill` adds a Claude Code skill (`skills/asset-shelf/SKILL.md`) so a coding agent in a game project
finds and pulls assets the same way, and never copies from the library by hand.

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
| `tools/ShelfIndex` | indexes one Unreal pack without Unreal (CUE4Parse; only the files' headers are read); `export --job` writes a mesh, character or animation as .glb, its textures as PNG and how its materials use them (materials.json), also without Unreal |
| `tools/index_raw.py` | indexes one pack of loose files with headless Blender |
| `tools/safety_scan.py` | lists what in a pack can run code (startup scripts, binaries, C++, plugins, config commands) |
| `tools/export_fbx.py` | exports one Unreal asset to FBX and its textures, in a small throwaway project |
| `tools/fab_sync.py` | Epic sign-in and the Fab library sync |
| `tools/itch_sync.py` | itch.io sign-in and library sync |
| `tools/kitbash_sync.py` | the KitBash3D kits you bought, downloaded with your browser session (command line only) |
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
- **KitBash3D sync** (`tools/kitbash_sync.py`, command line only) has no public API to use: it asks the site what its
  own Download button asks, with the session of your browser, kept in the keyring. It can stop working when the site
  changes. Mind your licence tier before putting those kits in a shared library.

## Contributing

Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT, see `LICENSE`. Third-party parts and their licences are listed in `NOTICE.md`.
