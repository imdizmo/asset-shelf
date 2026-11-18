# ShelfServer

The team's library service. It serves an Asset Shelf library (`index/` and `packs/`) read-only, and keeps the maps and
props teammates publish (zips, kept as they are sent). It recognises callers by Tailscale and takes no passwords.

## Who may do what

- The server listens only on a Tailscale address (100.64.0.0/10), or on loopback for tests.
- For each connection it asks the local Tailscale daemon (`/localapi/v0/whois` on its unix socket) who owns the peer
  address taken from the socket. Tagged devices are not people and are refused.
- Roles come from the team list, `team.json` version 1 (`{ "members": [ { "login", "name", "role" } ] }`). Another
  service on the same machine can write it; it is re-read when it changes. Roles: `observer` reads; `member`, `admin`
  and `owner` also publish. People Tailscale recognises but who are not on the list get only `/team/whoami`.

## The team's projects

`projects.json`, in the same folder as `team.json` unless `--projects <file>` (or `SHELF_PROJECTS_FILE`) names another
place (in a container, where the team list's folder belongs to Questlog, keep it in the server's own data), lists the
team's projects for the launcher. The owner edits it by
hand, and it is re-read when it changes:

```json
{ "version": 1, "projects": [ { "id": "game", "name": "The Game", "description": "…",
  "git": "https://example.com/owner/game.git", "lfs": "game", "setup": "tools/setup.sh",
  "branch": "main", "folder": "Game", "memoryMax": "16G" } ] }
```

`setup` is the path of a script inside the repository, never a command: this file only chooses which of the
repository's own files runs. An entry without a valid id, git URL (`https://…` or `git@host:…`) or setup path is
dropped. An optional field that does not check out is left out.

## API (plain http, port 8781; errors are JSON `{ "error": "<a sentence for a person>" }`)

| Request | Answer |
|---|---|
| `GET /healthz` | `ok`, to anyone: the server is up |
| `GET /team/whoami` | `{ login, name, role or null, service: "shelf", version, teamServer }` |
| `GET /team/projects` | `{ schema: 1, projects: [ { id, name, description, git, lfs (full URL or null), setup, branch?, folder?, memoryMax? } ] }` from `projects.json` beside the team list; members only |
| `POST /team/crashes?tool=…&version=…&os=…` | a crash report, one zip of at most 20 MB, from any member: `{ id }`. The newest 500 (at most 1 GB) are kept |
| `GET /team/crashes`, `GET /team/crashes/{id}`, `DELETE /team/crashes/{id}` | owner and admin: the reports, newest first (`{ id, tool, version, os, login, name, at, size }`), one report's zip, and deleting one |
| `GET /team/server` | any member: `{ service, version, port, started, diskFree, storage: { packs, index, lfs, maps, props, crashes, measuredAt } }` (bytes; measured in the background) |
| `PUT /team/projects/{id}`, `DELETE /team/projects/{id}` | owner and admin, with `X-Jatify: 1` (and a JSON body for PUT): add, change or remove a project in `projects.json` |
| `POST /team/playlogs?game=…&build=…` | a playtest log (gzipped `.playlog`, at most 20 MB) from any member: its facts, 201 (200 when that session was already sent). 400 when it is not a playlog for that game |
| `GET /team/playlogs[?game=…]`, `GET /team/playlogs/{id}` | any member: `[ { id, game, build, map, player, started, length, ended, counts, partial, size } ]` newest first, and one log |
| `DELETE /team/playlogs/{id}` | owner, admin or whoever sent it, with `X-Jatify: 1` |
| `GET /library` | `[ { id, files: [ { name, size, mtime } ] } ]`: each pack's top index files |
| `GET /library/{pack}/index/{path}` | a file from `index/{pack}/` |
| `GET /library/{pack}/files` | `[ { path, size, mtime } ]`: every file of `packs/{pack}/` |
| `GET /library/{pack}/files/{path}` | a file from `packs/{pack}/` |
| `GET /maps`, `GET /props` | the newest version of each: `[ { id, name, version, size, sha256, publishedBy: { login, name }, publishedAt } ]` |
| `PUT /maps/{id}?name=…` (or `/props/…`) | the zip as the body; `If-Match: <version>` refuses (409) when someone published since. 201 new, 200 new version |
| `GET /maps/{id}/pack[?version=n]` | the zip, `ETag: "<sha256>"` |
| `GET /maps/{id}` | `{ id, versions: [ rows as above, oldest first ] }` |
| `GET /maps/{id}/picture[?version=n]` | `picture.png` (or `thumbnail`, `preview`, `cover`; `.png` or `.jpg`) from inside the zip, at most one folder deep; 404 when there is none |
| `DELETE /maps/{id}[?version=n]` | owner and admin only: one version, or all of them |

Ids are 1–64 of `A–Z a–z 0–9 - _`. An upload is kept only once all of it has arrived and its zip directory reads;
the newest 5 versions are kept. Mtimes are Unix milliseconds. Links and hidden files in the library are never served.

## Git LFS

Each project has an LFS endpoint at `http://<server>:8781/lfs/<project>` (the project name follows the id rule
above). A repository points at it in its `.lfsconfig`:

```ini
[lfs]
    url = http://<server>:8781/lfs/<project>
    locksverify = true
```

It serves the Batch API with the basic transfer, and File Locking (`git lfs lock`, `locks`, `unlock`). Observers
download and see locks; members and up also push and lock; an admin or owner can `unlock --force` someone else's lock.
An object is stored once its size and sha256 match its oid, under `data/lfs/<project>/objects/`, and is never
changed or deleted. The server never answers 401, so git-lfs does not ask for a password (`server/test-lfs.sh` checks
this with a real git-lfs).

## Running it

```sh
server/test.sh                                  # the check: every route, on loopback
server/test-lfs.sh                              # git-lfs push, clone, lock and unlock against it
server/deploy.sh <ssh-host>:<folder>            # build for linux-arm64 (or pass a rid), copy it there, restart it
```

A release carries `ShelfServer-<version>-linux-arm64.tar.gz`. To update a running server, unpack it anywhere and run
`./update.sh <unpacked>/bin/ShelfServer` in the server's folder: it stops the server, keeps the old program as
`bin/ShelfServer.old`, and starts the new one. `data/` and `shelf.env` are kept. `GET /team/whoami` reports the
version, and the app says when the server is older than it needs.

Backups: copy `data/` and `shelf.env`. A copy can be made while the server runs. LFS objects never change once
written. Uploads land in hidden `.upload-*` files and `meta.json`/`locks.json` are replaced by a rename, so skip
dot-files. Something published during the copy can be caught with its new `meta.json` but without its zip. Stop the
server for the copy (or snapshot the volume) when that matters.

On the server, write `<folder>/shelf.env` once (`SHELF_LISTEN`, `SHELF_LIBRARY`, `SHELF_TEAM_FILE`; see `start.sh`),
then `./start.sh` and `./stop.sh`. `--listen tailscale:8781` uses this machine's own Tailscale address. Options: `--max-upload <bytes>` (4 GiB), `--tailscale-socket <path>`, `--hosts <name[:port],...>` (more names it
answers to; its address and Tailscale names are known already).
