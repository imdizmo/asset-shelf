# ShelfServer changelog

The team's library service (see [README.md](README.md)). Its version is its own, and is reported at `/team/whoami`.
Before 0.3.0 it was not released as a package. It ran only on the team's own server while it was being built.

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
