# InfraOrgIT Desktop — development

Technical notes for the Electron app. The user-facing overview is in
[`../README.md`](../README.md); command and security notes for agents are in
[`../CLAUDE.md`](../CLAUDE.md).

InfraOrgIT Desktop opens one or more instances in a single control-room window
and manages local SSH tunnels with the user's SSH agent. The main process owns
the windows, the authentication flow, the token store and the `ssh` processes;
renderers only see the narrow bridge exposed by the preloads.

## Structure

```
electron/
  main.js                 app lifecycle, main window, WebContentsView tabs, IPC
  auth/device-client.js   OAuth device flow + PKCE, injected fetch/delay
  auth/session-client.js  /auth/desktop-session and /api/session contract
  auth/refresh-store.js   safeStorage encrypted refresh-token file
  auth/desktop-auth.js    BFF/device restore, tunnel access tokens
  shell/instances.js      validation, atomic instances.json, old-list migration
  shell/settings.js       atomic settings.json (theme fallback)
  tunnels.js              SSH argument builder, process lifecycle, port guard
  tunnel-projection.js    /api/tunnels start/stop/heartbeat projection
  manager.html/js/css     control-room landing, tabs, drawer, palette, modal
  preload.js              narrow SPA bridge, origin-gated
  manager-preload.js      explicit shell IPC surface
  test/                   shell test, fake SSH, unit tests
```

There is no build step: Electron loads `main.js` and the local manager page
directly.

## Run

Requirements: Node 24+, npm, the OpenSSH client, a running SSH agent and an
InfraOrgIT instance. The local full stack (`make full` from `AllInfraOrgIT`)
serves `https://localhost:8443`.

```bash
cd electron
npm install
npm start
```

`DESKTOP_USER_DATA=/tmp/dir` redirects instances, settings and the encrypted
token archive. Packaged builds require a trusted certificate for the instance;
`DESKTOP_ALLOW_INSECURE_LOCALHOST=1` is the explicit development override for
the self-signed Caddy certificate. Loopback HTTP is accepted in development;
remote instances must use HTTPS.

## Tests

```bash
npm run test:unit     # node --test, no display needed
npm test              # unit + Electron shell test against https://localhost:8443
npm run tunnel-test   # fake SSH arguments and lifecycle
```

The unit suite covers the device client (discovery, PKCE, polling,
`slow_down`, refresh, error mapping), the BFF session exchange (request body,
cookie landing, 401/404/502 fallback, no token in errors), the safeStorage
store, instance/settings persistence, downloads and duplicate local-port
protection.

`npm test` starts Electron against the local tenant and writes screenshots to
`test/*.png` (ignored). The shell test seeds three fictitious instances and a
fake SSH process; it never opens a real tunnel. `PROTO_TEST=1` and
`PROTO_CLEAN=1` isolate the test user data, and `PROTO_URL`/`PROTO_SECOND_URL`
change the instances. The running stack may not serve `/auth/desktop-session`:
the shell then exercises the login fallback, while the unit tests cover the
session exchange with an injected fetch.

## Authentication and session storage

- At startup the main process checks `/api/session` through the instance
  partition. An existing BFF session is reused for the tunnel API; otherwise
  the OAuth 2.0 device flow with `infraorgit-mcp-desktop` and scopes
  `openid profile email offline_access mcp:read tunnels:operate` runs and opens
  the verification URL in the default browser.
- Only the refresh token is persisted, encrypted with Electron
  `safeStorage`, in `userData/desktop-auth.bin`. Access tokens, device codes,
  PKCE verifiers and CSRF tokens stay in the main process.
- On launch the main process refreshes the token with Keycloak and calls
  `POST /auth/desktop-session` with the instance partition's
  `session.fetch` and `credentials: "include"`. Chromium stores the BFF cookie
  in that partition, so the tab opens without the Keycloak page.
- The exchange repeats every six hours while an instance tab is open and after
  system resume. A revoked or expired refresh token clears the local session
  and falls back to `/auth/login`; `401/404/502` and network errors do the
  same.
- safeStorage uses the macOS Keychain, Windows DPAPI or Linux Secret Service.
  When Linux reports the `basic_text` backend the store refuses to write and
  keeps the session in memory for that run. A native keychain wrapper remains
  the next hardening step.
- Instances live in `userData/instances.json` and settings in
  `userData/settings.json`; both saves are atomic and contain no tokens.

## Tunnels

- `tunnels.js` builds the `ssh` argument list as an array (never a shell
  string) and spawns the system client without `stdio` access to a shell. The
  user's agent provides the keys; the app never reads or stores private keys.
- Start uses only the API allowlist for that instance: a server, deployment,
  service or target missing from the allowlist is rejected before `ssh` runs.
  Local, dynamic (SOCKS) and remote forwards map to `-L`, `-D` and `-R`; `-J`
  chains come from the API nearest-first.
- A duplicate local port is rejected before spawning a second `ssh`.
- Before start the manager reads `/api/connection-policy`. `Client` uses the
  system `ssh`; `Server` opens a loopback listener and relays each TCP
  connection over a WebSocket to `/api/tunnels/relay`; `Auto` tests the first
  SSH hop and falls back to the relay when the client cannot reach it. The
  relay accepts only Local services or deployments with a fixed target port;
  Dynamic and Remote stay client-only.
- The policy row is shared by all API nodes in a cluster. Relay WebSockets are
  node-local and each local TCP connection gets its own relay, so no sticky
  sessions are required; the load balancer must forward WebSocket upgrades.
- Start, active heartbeat (every 30 seconds), stop and error are projected to
  the instance's `/api/tunnels` endpoints with the signed-in BFF session and
  CSRF token, so the central `/tunnels` page shows the same session.
- Tunnel rows kept as “stopped” live in memory only. Ports can still be
  occupied by another process outside the app; `ExitOnForwardFailure` reports
  the failure back to the UI.

## Packaging

Packaging uses `@electron/packager` (dev dependency, pinned in
`package.json`):

```bash
npm run package        # current platform and architecture
npm run package:mac    # darwin/arm64
npm run package:win    # win32/x64
npm run package:linux  # linux/x64
```

The scripts use `electron/scripts/package.js`, which selects the platform icon
(`assets/icon.icns` on macOS, `assets/icon.ico` on Windows, `assets/icon.png`
for the Linux window). `python3 scripts/make-icons.py` regenerates all three
from the brand mark. The output directory is
`electron/dist/InfraOrgIT-<platform>-<arch>/`. macOS
produces `InfraOrgIT.app` inside `InfraOrgIT-darwin-<arch>/`, Windows
`InfraOrgIT.exe` inside `InfraOrgIT-win32-<arch>/` and Linux the `InfraOrgIT`
binary inside `InfraOrgIT-linux-<arch>/`. The scripts pass `--asar`,
`--overwrite` and the bundle id `it.infraorgit.desktop`, and ignore `test/`,
`README.md` and `package-lock.json`; production dependencies such as `ws` are
packaged. `dist/` is ignored.

## CI

`.github/workflows/ci.yml` runs the `build-test` job on `ubuntu-latest`,
`macos-latest` and `windows-latest`. Each job installs Node 24, runs
`npm ci --prefix electron`, `npm --prefix electron run test:unit`, then
`npm --prefix electron run package` and checks that the platform's packaged
application exists. The package build is an unsigned smoke test: CI does not
upload artifacts. There is no .NET setup or build.

## Publish

`.github/workflows/publish.yml` runs manually (`version` input, default
`0.1.0`) or on a `v*` tag. Tags are created by hand from `main`, never by the
workflow.

1. `version` strips a leading `v` and validates `^[0-9A-Za-z.+-]+$`.
2. Each platform job checks out the full history, and on a tag asserts that
   `GITHUB_SHA` is an ancestor of `origin/main` before packaging.
3. `npm ci --prefix electron`, then `node scripts/package.js` from `electron/`
   with the matrix platform/architecture, output to the repository `dist`,
   `--app-version`, the bundle id, the platform icon and the same ignores as
   the package scripts.
4. The archive is built with these names and roots:

   | Target | Runner | Archive | Root |
   |---|---|---|---|
   | `osx-arm64` | `macos-latest` | `InfraOrgIT-Desktop-<version>-osx-arm64.zip` | `InfraOrgIT.app` |
   | `osx-x64` | `macos-15-intel` | `InfraOrgIT-Desktop-<version>-osx-x64.zip` | `InfraOrgIT.app` |
   | `win-x64` | `windows-latest` | `InfraOrgIT-Desktop-<version>-win-x64.zip` | `InfraOrgIT-win32-x64/` |
   | `linux-x64` | `ubuntu-latest` | `InfraOrgIT-Desktop-<version>-linux-x64.tar.gz` | `InfraOrgIT-linux-x64/` |

   macOS uses `ditto -c -k --keepParent`, Windows `Compress-Archive` on the
   package folder and Linux `tar -czf ... -C dist InfraOrgIT-linux-x64`.
5. A `.sha256` checksum is written next to every archive.
6. Each platform uploads its archives and checksums as a workflow artifact
   with 14-day retention. Manual runs stop there.
7. On a `v*` tag the release job downloads the artifacts and creates or
   updates the GitHub Release with `softprops/action-gh-release@v2`. It is the
   only job with `contents: write`.

Packages are unsigned. Developer ID signing, macOS notarization, Windows
Authenticode signing and auto-update are follow-ups; until then the release
notes and root README must keep the Gatekeeper/SmartScreen warning and users
must verify the published checksum. When signing is added, keep the signing
material in repository secrets and never in the repository.

### Manual release

1. Merge the change through a pull request into `dev`, then `dev` into `main`,
   and wait for the `build-test` checks.
2. From `main`, create and push the tag by hand:
   `git tag -a v0.3.1 -m "InfraOrgIT Desktop 0.3.1" && git push origin v0.3.1`.
3. The `publish` workflow builds all four targets, writes the archives and
   checksums, and creates or updates the `v0.3.1` GitHub Release. Re-running
   the workflow for the same tag updates the release instead of creating a new
   tag.

## Decisions

See `../AllInfraOrgIT/docs/adr/010-mcp-server-and-tunnel-app.md`: tunnels live
in the desktop app, use the system SSH agent and only open allowlisted targets.
