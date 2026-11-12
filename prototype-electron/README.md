# InfraOrgIT Desktop — Electron prototype

Experimental Electron shell for InfraOrgIT Desktop. It exists to validate the
replacement of Photino before any production rewrite. It is **not** the product
desktop yet.

## What it proves

Tested against the local Caddy stack on macOS 27 arm64:

- Electron/Chromium loads `https://localhost:8443` and the real InfraOrgIT
  login page; the main SPA reaches the dashboard after login.
- Two instance windows get separate cookie/session partitions.
- A child bridge exposes `window.infraorgitDesktop` (and a legacy
  `window.external` shim) through `contextBridge`; the web tunnel panel detects
  it and renders `/tunnels`.
- The tunnel manager builds and launches `ssh -L/-D/-R` commands through the
  user's SSH agent; the test uses a fake `ssh` binary to assert the exact args.
- The manager window lists multiple instances and can open a separate window
  for each.

Photino 4.0.16 failed the HTTPS navigation in the same environment even with a
trusted Caddy CA and certificate-error handling; see the desktop repository
history for that isolated probe. Electron is used here to test the
replacement, not to hide that finding.

## Run

Requirements: Node 24+, npm, the local full stack (`make full`) on
`https://localhost:8443`, and a trusted Caddy CA or an explicit localhost
exception. The prototype allows localhost certificate errors only; production
must use a trusted certificate.

```bash
cd InfraOrgITDesktop/prototype-electron
npm install
npm start
```

The manager window opens. Add or keep `Locale` (`https://localhost:8443`) and
press **Apri**. The login uses the local dev user; the test defaults are
`admin`/`dev-admin` and can be overridden with `PROTO_USER`/`PROTO_PASSWORD`.

### Tests

```bash
npm test          # Electron loads/login/main FE/tunnels, two windows
npm run tunnel-test
```

`npm test` writes `test/screen.png` and `test/tunnels.png` locally (ignored).
The tunnel test uses `test/fake-ssh.js`; it never opens a real connection.

## Structure

```
main.js              Electron main process, windows, sessions, IPC
preload.js           narrow contextBridge for the instance SPA
manager-preload.js   narrow contextBridge for the local manager
manager.html/js      instances and launch controls
tunnels.js           SSH argument builder and process lifecycle
test/                fake SSH and Electron verification
```

## Security boundaries

- `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`.
- The instance page sees only `infraorgitDesktop.sendMessage` and
  `receiveMessage`; no Node, filesystem or `ipcRenderer` object.
- The certificate exception is limited to loopback hosts and can be disabled
  with `PROTO_ALLOW_INSECURE_LOCALHOST=0`.
- Tunnel processes are spawned by the main process; the renderer never passes
  a shell string. Arguments are built as an array.
- Tokens are held in main-process memory for the prototype; no persistence.

## Not implemented yet

- Keycloak device flow in the SPA (the panel currently accepts a personal
  token; the manager prototype does not perform device login).
- Real tunnel event/session reporting, autoStart, reconnect and persistence.
- App auto-update, macOS/Windows signing/notarization, installers.
- Full permission review for every IPC message and production certificate
  policy.
- Automated GUI test for a real SSH target; only the argument/IPC path is
  covered.

## Packaging path (next step)

Use Electron Forge or electron-builder to produce signed macOS `.app`/DMG and
Windows installers, with the same `preload`/IPC boundary. Keep the localhost
certificate exception out of production builds and use a trusted certificate
for Caddy. Do not disable Chromium sandboxing to work around a packaging issue.
