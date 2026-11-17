# Running ShelfServer with Docker

This runs the team's library service on any Linux machine with Docker: a home server, a NAS that has Docker, or a
small cloud machine. It's reachable only over your [Tailscale](https://tailscale.com) network, and it knows who is
calling from Tailscale, so nobody signs in.

Two containers run side by side. Tailscale's official image joins your tailnet, and ShelfServer shares its network,
so the server sees each caller's real tailnet address.

## Steps

1. **An auth key.** In the Tailscale admin console, under Settings > Keys, make an auth key (one-off is fine). Put it
   in a file named `.env` next to `compose.yaml`:

   ```
   TS_AUTHKEY=tskey-auth-...
   ```

2. **Folders.** Next to `compose.yaml`, make `data/` and `team/`. The server runs as user 1654, so give it the
   folders:

   ```sh
   mkdir -p data team && sudo chown -R 1654:1654 data team
   ```

3. **The team list,** `team/team.json`. If a Questlog server on the same machine writes it, point the volume at that
   folder instead. Otherwise write it yourself. Use each person's Tailscale login as Tailscale shows it, with a role
   of `owner`, `admin`, `member` or `observer`:

   ```json
   { "version": 1, "members": [
     { "login": "you@example.com", "name": "You", "role": "owner" } ] }
   ```

4. **Start it:**

   ```sh
   docker compose up -d
   docker compose logs shelf     # "ShelfServer x.y.z on http://100.x.y.z:8781/ …"
   ```

   The machine appears in your tailnet as `shelf`. Point your tools at `shelf` (or its 100.x address); the port is
   8781.

## Sharing an Asset Shelf library

Uncomment the library volume and the `command` line in `compose.yaml`, with the folder that holds `index/` and
`packs/`. It's read-only for the server.

## Updating

```sh
docker compose pull && docker compose up -d
```

Your data stays in `data/` and `team/`. Back up those two folders: copying while it runs is fine if you skip dot-files.

## Notes

- `TS_USERSPACE: "false"` and the tun device matter. In Tailscale's userspace mode every caller would arrive from
  127.0.0.1, and the server would recognise nobody.
- The server refuses requests that name another host. If you reach it by a name Tailscale doesn't know (your own DNS,
  say), add it with `--hosts name:8781` in the `command` line.
