# InfraOrgITDesktop — agent guide

Desktop app for InfraOrgIT: an Electron shell in `electron/`. Sibling of
`../InfraOrgITApi`, `../InfraOrgITWeb`, `../InfraOrgITDocker` and
`../AllInfraOrgIT`.

## Commands

- `npm --prefix electron start`: start the app (`npm install` first).
- `npm --prefix electron run test:unit`: unit tests, no display needed.
- `npm --prefix electron run test:shell`: Electron shell test; needs the local
  stack on `https://localhost:8443` (`make full` from `AllInfraOrgIT`).
- `npm --prefix electron test`: unit + shell tests.
- `npm --prefix electron run tunnel-test`: fake SSH arguments and lifecycle.
- `npm --prefix electron run package`: package the current platform into
  `electron/dist/`. `package:mac`, `package:win` and `package:linux` select a
  target explicitly.
- `node --check electron/main.js` (and any other changed JS file) is the quick
  syntax gate.
- `.github/workflows/ci.yml` (`build-test` on Linux, macOS and Windows) installs
  with `npm ci --prefix electron`, runs the unit suite and packages unsigned.
  `.github/workflows/publish.yml` packages the four release targets on a `v*`
  tag or a manual run.

## Rules

- UI copy is Italian; code identifiers and commit messages are English
  (Conventional Commits).
- The app never stores passwords or private keys. Tunnels use the user's
  `ssh`/agent and only allowlisted targets returned by the instance API.
- Tokens live only in the main process. The refresh token is encrypted with
  Electron `safeStorage` in `userData/desktop-auth.bin`; access tokens, device
  codes, PKCE verifiers and CSRF tokens never reach a renderer. Never write
  tokens to `instances.json`, logs or a child process argument list. If the
  protected store is unavailable, keep the session in memory for that run.
- `contextIsolation: true`, `nodeIntegration: false` and `sandbox: true` on
  every renderer. The preload exposes only the narrow
  `window.infraorgitDesktop` JSON bridge, gated by the instance origin. Never
  expose `ipcRenderer`, `require`, `process` or Node APIs, and never disable the
  Chromium sandbox to work around packaging.
- `shell/instances.js` accepts `https` for every host and `http` only for
  loopback (`localhost`, `127.0.0.1`, `[::1]`), with no credentials, query or
  fragment. Do not weaken that check; `DESKTOP_ALLOW_INSECURE_LOCALHOST=1` is
  the development-only certificate override.
- `tunnels.js` spawns the system `ssh` with an argument array, never a shell
  string; keep `ExitOnForwardFailure` and host-key verification intact. The
  tunnel manager owns processes, ports and the live projection to
  `/api/tunnels`.
- `shell/instances.js` and `shell/settings.js` are the only file persistence:
  keep saves atomic, validate every value and never write secrets. Tunnel rows
  kept as “stopped” are in memory only.
- Release packages are unsigned: Developer ID signing, notarization, Windows
  Authenticode signing and auto-update are follow-ups. Keep the
  Gatekeeper/SmartScreen warning and the `.sha256` checksums accurate; never
  claim a package is signed.
- `node_modules/` and `dist/` are ignored; never commit them.
- Keep the root `README.md`, `electron/README.md` and `docs/DEVELOPMENT.md`
  aligned when commands, packages or security boundaries change.
