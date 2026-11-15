# Contributing to Asset Shelf

Thanks for wanting to help. Bug reports, ideas, documentation fixes and code are all welcome.

Asset Shelf is one of the Jatify tools (Groundwork, Asset Shelf, Prop Generator and the Jatify Launcher). They share one
look and the same way of working, so this guide is nearly the same in each repository.

## Reporting a bug

Open an issue and say:

- what you did, what you expected and what happened instead;
- the version (the release you downloaded, for example 0.1.5) and whether you are on Linux or Windows;
- the log (View > Debug log) or a screenshot when you have one.

A small file you made yourself that reproduces the problem helps most, for example a test `.uasset`, `.fbx` or
`.blend`. **Never attach anything from your asset library**: no pack files, no `index.json` or `pack.json`, no
thumbnails or store pictures from paid packs, not even one asset. Name the pack by its store listing instead. The
debug log and screenshots show pack names and paths: crop or blank them if you would rather not share them.

**Security problems do not go in a public issue.** Report them privately through the repository's Security tab
(**Report a vulnerability**).

## Suggesting a change

Open an issue before writing a larger change, so nobody builds something that will not be merged. Say what you want
to do and why. Small fixes (a typo, a clear bug, a missing check) can go straight to a pull request.

## Building and testing

You need the .NET 10 SDK and git (the repository has a submodule). Blender 5.1 or later on the `PATH` is needed for
loose files, materials, turntables and Unity sends; Python 3.11 or later for the indexing and sync tools. Unreal
Engine 5.8 or later is only needed for Send to Unreal and for the asset kinds the app cannot export by itself
(effects, blueprints, maps).

```sh
git clone --recurse-submodules https://github.com/imdizmo/asset-shelf.git
cd asset-shelf
dotnet build app -c Release
dotnet build tools/ShelfIndex -c Release
python3 -m venv tools/.venv && tools/.venv/bin/pip install -r tools/requirements.txt
dotnet run --project app -c Release
```

There are no automated tests yet. Check a change by running the app on a small library of your own and doing what
you changed; for the exporter, export a few assets with `ShelfIndex export --job <job.json>` (the job format is at
the top of `tools/ShelfIndex/Export.cs`) and open the result in Blender. Say in the pull request what you tried.

The app needs a library to show: a folder with `packs/` and `index/` (the layout is in the README). Point `Packs` and
`Index` in the settings at it and index your own packs with `tools/index_library.py`. CUE4Parse is built from
`third_party/CUE4Parse`, a fork with fixes for editor packages; a change there is a separate pull request to that
fork.

## Making a pull request

1. Fork the repository and make a branch from `main`. Name it for what it does, for example
   `fix/drawer-resize` or `feat/pack-filter`.
2. Keep one pull request to one change. A fix and an unrelated tidy-up are two pull requests.
3. When you change behaviour, say in the pull request how you checked it (there is no test project yet).
4. Update the documentation and the README when what the user sees changes.
5. Make sure the build has no new warnings.
6. In the pull request, say what changed and how you checked it. For anything visible, add a before and after
   screenshot.

Commit messages are plain sentences that say what changed for the user, as in the existing history, for example
"The objects drawer can be docked on the left". No prefixes or ticket numbers are needed.

## Code style

- C# with nullable reference types on. Follow the style of the file you are editing.
- The interface is built in code, not XAML. Keep it that way.
- Comments say why, not what. Do not leave commented-out code.
- No new dependency for something a few lines can do. If you do need one, say why in the pull request and check that
  its licence allows redistribution.
- Anything that reads a file from someone else (a map, a pack, a preset, a plugin) must treat it as untrusted: stay
  inside its folder, check sizes, and never run code from it without asking the user.

## The look

All four tools use one design. Colours, sizes, radii, type and shadows come from `Tokens.cs`, which is the same file
in every tool with only the namespace changed.

- Use a token. Do not write a colour or a size as a literal in a control.
- Do not edit `Tokens.cs` in one tool only. A change to the tokens is a change to all four tools: open an issue first.
- Filled orange is for the one main action on a screen. A chosen tool or item uses the selected wash.
- Every control needs a visible keyboard focus state and a label a screen reader can read.
- Text is sentence case, in plain words, and says what a button does.

In Asset Shelf the tokens are in `app/Look/Tokens.cs`; `app/Look/Theme.cs` maps the older names onto them.

## Assets and licences

- Only add files you made yourself or whose licence allows redistribution, and list them in `NOTICE.md` with their
  source and licence.
- Never add files taken from a game or from a paid asset pack, and never add generated copies of them.
- Library data never goes into this repository, an issue or a pull request: no pack files, no indexes or thumbnails
  from a real library, no store pictures, and no pack names in test data or screenshots. Test files are ones you
  made yourself.

## Licence

By contributing you agree that your work is released under the MIT licence of this project.

## Getting help

Ask in an issue.
