# ShelfServer changelog

The team's library service (see [README.md](README.md)). Its version is its own, and is reported at `/team/whoami`.
From 0.5.0 it ships as part of the Jatify team server (imdizmo/jatify-team-server), not with Asset Shelf's releases.
Before 0.3.0 it was not released as a package. It ran only on the team's own server while it was being built.

## [0.5.0] - not released yet

- **For a launcher's Server tab:** `GET /team/server` shows the server's version, when it started, free disk and how
  much each area holds (measured in the background). Owners and admins can add, change and remove the team's
  projects (`PUT`/`DELETE /team/projects/<id>`), without editing `projects.json` by hand.
- `--listen tailscale[:port]` finds this machine's Tailscale address by itself.
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
