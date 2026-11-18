# Changelog

What each Asset Shelf release lets you do, in plain words. The team server, ShelfServer, has its own versions and its
own list: [server/CHANGELOG.md](server/CHANGELOG.md). Each release's notes on GitHub are its section below.

Entries before 0.2.0 were written afterwards, from the git history.

## [0.3.0] - 2026-11-18

### New

- **`shelf`, Asset Shelf from the command line:** search the library, show a pack or an asset (licence, credit,
  conditions, original source, picture), and pull an asset into a project as a model file with its textures, into an
  Unreal project, or as its loose files. Each pull records where it came from and copies the pack's licence text. It
  refuses a pack whose safety scan found code unless you insist. It uses the team library when a team server is set.
- **Search by meaning, as an add-on:** `shelf-meaning`, installed on its own (its own download on the release page), finds assets by a description or by
  likeness to another asset, from their pictures, with a model run on this computer. Asset Shelf and `shelf` work
  without it and carry none of its libraries; `shelf search --meaning` uses it when it is there.
- **A sound browser.** A pack's sounds can be measured from their own audio, no AI involved: length, how loud they
  get, a pitch estimate, how bright they sound, attack, whether they loop cleanly, mono or stereo. The Sounds tab then
  filters and orders by those (a new Sound group), a sound's details show them, and **Play** plays it through the
  system's player. `shelf search --sound long,low,oneshot …` does the same for an agent. You still pick every sound.
  Packs are measured by `tools/index_library.py --sounds` (new packs as they are indexed).
- **Mesh prep (optional, with Blender):** Prepare… on a mesh or character looks the model over (holes, faces turned
  the wrong way, stray specks, separate parts, triangles, size), and can repair it, lighten it to a triangle budget
  keeping the shape, make lighter LODs and a collision shape, and export it for Unreal, Godot, Groundwork or Blender.
  A report says what was found and done, and the axes and units used. The asset itself is never changed: everything
  goes into a folder you choose. `shelf prep` does the same. Without Blender the button is not there.
- **Free assets from Poly Haven and ambientCG** (File > Get free assets…): search a source's catalog, tick what
  you want, choose the texture resolution, and the ticked assets are downloaded (every file checked), indexed, and
  filed with where they came from, who made them and their CC0 licence (a copy of the text is kept). An asset already
  downloaded is marked, and one changed since shows as updated. A pack of texture maps becomes a material in the grid.
- **A Claude Code skill** (`shelf install-skill`), so a coding agent in a game project finds and pulls assets the same
  way.

- **Crash reports.** If Asset Shelf closed unexpectedly, the next start asks whether to send that run's log to your
  team's server. You can see exactly what is sent first. Nothing is sent unless you click Send, and nothing is asked
  when no team server is set. Each run now keeps its log in `~/.cache/asset-shelf/runs` (the last 10 runs).
- **Assets from sites that collect other people's assets.** Mark a pack's site as one in Origin…, or let Asset Shelf
  recognise a known one. Each of its assets then gets an "Original" field in its details, for the creator and the page
  where it was first published, which also goes into the project's provenance record when you send it. A new Origin
  filter, "Originals not recorded", shows the packs with assets still missing one. Such sites have been found mixing in
  assets they had no right to, and the original source is what lets you trace and remove one.

### Not tested yet

- **None of this has been tried on real Windows.** The Windows build is made and packaged, but crash reports, `shelf`,
  the meaning add-on, the sound browser's Play, mesh prep and free sources were tested on Linux only.

## [0.2.0] - 2026-11-17

### New

- **Use your team's library from a team server.** Set the address in File > Team server…, or let the Jatify Launcher
  pass it on (`JATIFY_TEAM`). The server knows who you are through Tailscale, so there is no account to make and no
  password to type. The status bar shows who you are connected as and your role. If the server can't be reached,
  Asset Shelf says so and lets you retry or carry on with the library on your computer.
- **No file share needed for teammates.** Asset Shelf keeps a copy of the library's index on your computer, fetches
  pictures as they come into view, and downloads a pack's files only when you send one of its assets.
- **Your team's maps and props in the grid.** Maps published from Groundwork and props published from Prop Generator
  appear as two packs, "Team maps" and "Team props", with a new Props tab. Each one shows its picture (when it has one),
  who published it, when, and every kept version. "Save the zip…" puts a copy in your Downloads folder.
- **Delete from the team.** The team's owner and admins can delete one version, or a whole map or prop.
- **Where each pack came from.** Record a pack's origin, its licence text and its receipts (Origin… in the pack's
  details), with filters for packs whose origin, licence or receipt is missing.
- Asset Shelf says when the team server is older than it needs, and what won't work until it is updated.

### Fixed

- Names the team server sends are checked before anything is written to disk, so a misbehaving server can't write
  outside Asset Shelf's own cache folder.

### Not tested yet

- **The team connection has not been tried on real Windows.** It was tested on Linux against a real team server.

## [0.1.5] - 2026-11-15

### New

- **Export without Unreal.** Meshes, characters and every kind of animation can go to Blender, Unity and Groundwork
  without Unreal Engine installed. Unreal is only needed for Send to Unreal and for effects, blueprints and maps.
- A new look shared with the other Jatify tools: top bar, a split Send button that remembers the last target, tabs,
  tiles and status bar.

### Fixed

- Finding Unreal Engine trusts only Program Files. An install anywhere else is shown and asked about, also during the
  guided install. Paths with hidden or reversed characters are ignored, so the confirmation shows the real path.

## [0.1.4] - 2026-11-15

### New

- **Nothing to install for the tools.** The release carries its own Python, used before any Python on your system.
- **Unreal Engine installed for you on Windows.** When an export needs Unreal and none is found, a window installs the
  Epic Games Launcher (from Epic's official installer) and opens the engine's install. Asset Shelf waits for it, then
  carries on with the send. Exports use Unreal Engine 5.8 or later.

## [0.1.3] - 2026-11-15

### Fixed

- **Shared libraries connect on Windows.** Connecting opens the share with the account you give, replaces an older
  connection to the same server made under another account, and shows Windows' own error message instead of an
  empty library. The password is kept in Windows Credential Manager and used to connect again at start.

### Not tested yet

- Not tried on a real Windows machine when it was released.

## [0.1.2] - 2026-11-15

### New

- **Send to Groundwork.** Meshes become self-contained .glb files (textures inside, at most 2048 px; origin at the
  base, Y up) that go straight into Groundwork's inbox. The status bar shows Groundwork's answer.
- **Connect to a shared library** (File menu): a folder on a file server such as a NAS, with an account there. The
  password is kept by the system and used to reconnect at start. A library you can only read opens in member mode, so
  only its owner syncs and indexes it.

### Not tested yet

- Connecting on Windows, and on Linux through GNOME's gvfs-smb, had not been tried when it was released.

## [0.1.1] - 2026-11-14

### New

- A new icon: an orange library folder with a cube on the front.
- **Linux AppImage**, and an `install-desktop-entry.sh` in the tar.gz that adds Asset Shelf to your app menu.

## [0.1.0] - 2026-11-14

The first public release, for Linux and Windows.

- One grid for your whole asset library (Fab/Unreal packs and loose files), with kinds as tabs, filters by tag, pack
  and licence, search, collections, favourites, notes and saved filters.
- What comes with an asset (the packages it needs) and what works with it. Duplicates across packs are linked.
- A Packs view with each pack's store pictures, description and licence.
- Send to Unreal, Send to / Open in Blender (with materials wired), Send to Unity (URP, HDRP or Built-in materials),
  and Spin in 3D.
- Library sync from Fab and itch.io, with a preview before anything is downloaded.
- A safety scan that flags anything in a pack that could run code. Pictures are checked for size before they are
  decoded, and the Blender add-on strips scripted drivers from what it imports.
