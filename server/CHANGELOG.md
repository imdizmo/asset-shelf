# ShelfServer changelog

The team's library service (see [README.md](README.md)). Its version is its own, and is reported at `/team/whoami`.
From 0.5.0 it ships as part of the Jatify team server (imdizmo/jatify-team-server), not with Asset Shelf's releases.
Before 0.3.0 it was not released as a package. It ran only on the team's own server while it was being built.

## [0.7.0] - not released yet

### New

- **Suggestions from the team's tools.** A team member writes what to improve and adds screenshots; the tool sends
  them to `POST /team/suggestions` as one zip (`suggestion.txt` and at most 10 PNG or JPEG pictures, 20 MB at most).
  The owner and admins list them with the words shown (`GET /team/suggestions`), download one, and delete it. The
  store keeps at most 500 suggestions or 1 GB, dropping the oldest. Contract for the tools: `SUGGESTIONS.md` beside
  the team server's notes.
- Crash reports and suggestions may also say `os=web`, for a tool used in a browser.

## [0.6.1] - 2026-11-18

### Fixed

- **Started slowly, or not at all, with a large library in the Docker image.** The server watched its working folder
  for settings changes, which in the image is `/`. So before it listened, it put a watch on every folder under it,
  the library included: tens of thousands over a network mount, and close to its memory limit. It now watches only
  its own program folder.

## [0.6.0] - 2026-11-18

- **Playtest logs.** A game can send a playtest log (one gzipped `.playlog` of at most 20 MB, after the player agreed
  twice) to `POST /team/playlogs?game=…&build=…`. The server checks its header and counts its events once on arrival
  (deaths, kills, quest steps, crafts, builds, gathers, respawns, teleports; length; how it ended), so any member can
  list them with those facts and download one. The owner, admins and whoever sent a log can delete it. Who sent a log
  is kept beside it and never listed: the logs name only a random player id. The newest 2,000 (at most 2 GB) are
  kept. Each log is named by when the server received it and a hash of its bytes, so the same file sent again is kept
  once and no log can push out or stand in for another.
- **Uploads must say what they are.** A crash report needs `Content-Type: application/zip`, and a playtest log
  `application/gzip` (anything but plain text or a form). A web page open in the tailnet could otherwise post one in
  someone's name. A tool that sent a crash report without a content type gets 415 and has to add it.

## [0.5.0] - 2026-11-18

- **For a launcher's Server tab:** `GET /team/server` shows the server's version, when it started, free disk and how
  much each area holds (measured in the background). Owners and admins can add, change and remove the team's
  projects (`PUT`/`DELETE /team/projects/<id>`), without editing `projects.json` by hand.
- `--listen tailscale[:port]` finds this machine's Tailscale address by itself.
- `--projects <file>` (or `SHELF_PROJECTS_FILE`) keeps the team's project list somewhere other than beside the team
  list; a list the server may not write answers with a sentence instead of an error.
- `/healthz` answers "ok" to anyone, so a launcher or a container can tell the server is up. A line
  `SHELF_LISTENING <port>` is printed once it listens. `/team/whoami` and `/team/server` name the team server release
  it came from (`JATIFY_TEAM_SERVER`).
- **Runs on Windows too** (not tried on a real Windows machine yet). The web server is now ASP.NET Core's Kestrel
  instead of HttpListener, which on Windows needs an administrator's URL reservation. Where Tailscale's daemon socket
  isn't there (Windows, macOS), people are recognised through the `tailscale whois` command.

### Fixed

- A web page open on someone's computer in the tailnet could create or remove Git LFS file locks by posting plain
  text. Lock requests must now be JSON, as git-lfs sends them. The new admin changes need an `X-Jatify: 1` header,
  which a web page can't send. Deleting a crash report needs it too.
- The server answers only at its own names: its Tailscale address and MagicDNS names, plus any given with `--hosts`
  (or `SHELF_HOSTS`). A web page whose own domain was pointed at the server's address (DNS rebinding) could otherwise
  have passed for the server itself in the browser.
- JSON requests (project changes, LFS batch and locks) are capped while they are read, also when sent without a
  length.

## [0.4.1] - 2026-11-17, on the team's own server only

- The owner and admins can delete a crash report (`DELETE /team/crashes/<id>`).
- A crash report's file name holds a short code for the sender instead of their login. The login is kept in the
  `.json` beside it.

## [0.4.0] - 2026-11-17, on the team's own server only

- **Crash reports.** The team's tools can send a crash report (one zip, at most 20 MB) after the person who saw the
  crash clicks Send. Any team member can send one; the owner and admins list and download them at `/team/crashes`.
  The newest 500 reports (at most 1 GB) are kept.

## [0.3.0] - 2026-11-17

First release as a package: `ShelfServer-0.3.0-linux-arm64.tar.gz` (a self-contained program for an ARM64 Linux
machine on your tailnet, such as a NAS).

- **Who you are, without passwords.** People are recognised through Tailscale. Their role (owner, admin, member,
  observer) comes from the team list, `team.json`, which another team service (Questlog) writes.
- **The library, read-only,** for Asset Shelf: each pack's index, pictures and files.
- **Maps and props** that teammates publish from Groundwork and Prop Generator: each upload is a new version (the
  newest 5 are kept). Someone who publishes over a newer version is told so instead. Pictures from inside the zips
  are served too. Owners and admins can delete a version or a whole map or prop.
- **Git LFS for your projects,** with file locking, so the big files of a game repository can live on the team's own
  server. Nobody is ever asked for a password.
- **The team's projects** (`projects.json`, beside the team list), for the Jatify Launcher's project card.
- `update.sh` replaces the program and restarts it, keeping your data. `--version` prints the version.

### Not tested yet

- Clients on real Windows. The server itself was tested on an ARM64 NAS, with clients on Linux.

## [0.2.0] - 2026-11-16

Not released on its own. It added versions, pictures and delete for maps and props, `update.sh` and `--version`.

## [0.1.0] - 2026-11-16

Not released on its own. The first version: the library, maps and props, recognised by Tailscale; then Git LFS with
file locking.
