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

## API (plain http, port 8781; errors are JSON `{ "error": "<a sentence for a person>" }`)

| Request | Answer |
|---|---|
| `GET /team/whoami` | `{ login, name, role or null, service: "shelf", version }` |
| `GET /library` | `[ { id, files: [ { name, size, mtime } ] } ]`: each pack's top index files |
| `GET /library/{pack}/index/{path}` | a file from `index/{pack}/` |
| `GET /library/{pack}/files` | `[ { path, size, mtime } ]`: every file of `packs/{pack}/` |
| `GET /library/{pack}/files/{path}` | a file from `packs/{pack}/` |
| `GET /maps`, `GET /props` | the newest version of each: `[ { id, name, version, size, sha256, publishedBy: { login, name }, publishedAt } ]` |
| `PUT /maps/{id}?name=…` (or `/props/…`) | the zip as the body; `If-Match: <version>` refuses (409) when someone published since. 201 new, 200 new version |
| `GET /maps/{id}/pack[?version=n]` | the zip, `ETag: "<sha256>"` |

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
server/deploy.sh <ssh-host>:<folder>            # build for linux-arm64 (or pass a rid) and copy it there
```

On the server, write `<folder>/shelf.env` once (`SHELF_LISTEN`, `SHELF_LIBRARY`, `SHELF_TEAM_FILE`; see `start.sh`),
then `./start.sh` and `./stop.sh`. Options: `--max-upload <bytes>` (4 GiB), `--tailscale-socket <path>`.
