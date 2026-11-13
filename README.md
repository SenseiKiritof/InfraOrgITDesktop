# InfraOrgIT Desktop

InfraOrgIT Desktop is the Electron app in [`electron/`](electron/README.md). It
opens one or more InfraOrgIT instances from a single control-room window, reuses
the instance BFF session and drives the local SSH agent. No private key is
copied into the app.

![InfraOrgIT Desktop landing with instance cards and tabs](docs/screenshots/electron-landing.png)

The screenshots use fictitious demo instances, servers and tunnels.

## Features

- **Landing manager.** Instance cards show name, URL, environment, last use,
  server count and tunnel count. **Nuova istanza**, **Apri**, **Gestisci** and
  **Rimuovi** are in the card; **Istanze** is always one click away from a tab.
- **Isolated tabs.** Each instance URL has its own persistent Chromium
  partition. Cookies, the BFF login and storage never cross instances.
- **Tunnel drawer.** Rows are grouped by instance and server and show the local
  port. **Nuovo tunnel** searches by machine, IP, environment, application and
  service, keeping the environment groups. Start, stop, error and a 30-second
  heartbeat are projected to the central `/tunnels` page.
- **Session reuse.** If the instance tab is already signed in, the desktop uses
  that BFF session for the tunnel API. The device flow with `offline_access`,
  or a personal `tunnels:operate` token, is only a fallback.
- **One theme.** The shell follows the account theme. The Web toggle is the
  only control and cycles light, dark and system; the shell follows the
  resolved appearance. There is no desktop theme button.
- **Web workspace in a tab.** Server detail, SSH console, SFTP/FTPS/FTP file
  sessions and tunnel anagrafica stay in the existing Web app. The desktop adds
  the native window, instance management and the process-level tunnel manager.

![Global tunnel drawer grouped by environment with the searchable New tunnel form](docs/screenshots/electron-tunnels.png)

![Light theme inherited from the Web account preference](docs/screenshots/electron-light.png)

## Run locally

Requirements: Node 24+, npm, the OpenSSH client and a running SSH agent, plus an
InfraOrgIT instance. For the local full stack, `make full` from
`AllInfraOrgIT` serves `https://localhost:8443`.

```bash
cd electron
npm install
npm start
```

Packaged builds require the local Caddy certificate to be trusted by macOS. In
development the shell can accept loopback certificates;
`DESKTOP_ALLOW_INSECURE_LOCALHOST=1` is the explicit override for a packaged
local test.

For tests, packaging and the security boundaries, see
[`electron/README.md`](electron/README.md) and
[`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

## First start

1. Press **Nuova istanza**, enter a name and the HTTPS URL of the installation,
   and choose whether it opens at startup.
2. Press **Apri**. Each instance gets its own tab and persistent Chromium
   session.
3. Sign in from the instance tab. The tunnel drawer reuses that session; no
   second Keycloak prompt is needed. Use the device flow or a personal token
   only when the instance has no web session.
4. Open **Tunnel**, expand **Nuovo tunnel**, search for a server, deployment or
   service, set the local port and press **Avvia**.
5. Change the theme from the Web app. The desktop shell follows it.

## Security

- The refresh token is encrypted with Electron `safeStorage` in
  `userData/desktop-auth.bin`: macOS Keychain, Windows DPAPI or Linux Secret
  Service. Without a keyring the session stays in memory for that run.
- Access and refresh tokens never reach a renderer. The Web app receives only
  the CSRF token it needs for its own mutations. The preload exposes only the
  narrow `window.infraorgitDesktop` message bridge, gated by the instance
  origin.
- Remote instance URLs must use HTTPS. Loopback HTTP is accepted only during
  development; packaged builds require a trusted certificate unless the
  explicit local override is set.
- The app reads the SSH configuration and lets the system `ssh` use the agent;
  it does not store private keys.
- Tunnels are projected to the instance using the signed-in BFF session and
  CSRF token. A user without the operator role cannot see or change them.

## Packages

The `publish` workflow packages four targets:

| Platform | Archive | Contents |
|---|---|---|
| macOS Apple silicon | `InfraOrgIT-Desktop-<version>-osx-arm64.zip` | `InfraOrgIT.app` |
| macOS Intel | `InfraOrgIT-Desktop-<version>-osx-x64.zip` | `InfraOrgIT.app` |
| Windows x64 | `InfraOrgIT-Desktop-<version>-win-x64.zip` | `InfraOrgIT-win32-x64/` |
| Linux x64 | `InfraOrgIT-Desktop-<version>-linux-x64.tar.gz` | `InfraOrgIT-linux-x64/` |

- **macOS**: unzip and move `InfraOrgIT.app` to Applications.
- **Windows**: unzip and run `InfraOrgIT.exe` inside `InfraOrgIT-win32-x64`.
- **Linux**: extract and run `InfraOrgIT` inside `InfraOrgIT-linux-x64`.

A `<archive>.sha256` checksum is published next to every archive.

> The packages are unsigned. macOS Gatekeeper blocks a downloaded app until it
> is allowed in **System Settings → Privacy & Security** (or the quarantine
> attribute is removed with `xattr -dr com.apple.quarantine InfraOrgIT.app`),
> and Windows SmartScreen shows an unknown-publisher warning. Developer ID
> signing, macOS notarization, Windows Authenticode signing and auto-update are
> follow-ups. Verify the SHA-256 checksum before running an archive.

## Learn more

- [AllInfraOrgIT](https://github.com/SenseiKiritof/AllInfraOrgIT) — product,
  deployment and architecture.
- [`../AllInfraOrgIT/docs/architecture/mcp-server-and-tunnels.md`](../AllInfraOrgIT/docs/architecture/mcp-server-and-tunnels.md)
  — MCP and tunnel design.
- [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) — run, test, packaging, CI and
  publish.
- [`electron/README.md`](electron/README.md) — Electron structure, commands and
  security boundaries.
