# InfraOrgIT Desktop

Desktop shell for InfraOrgIT: opens one or more instances with the system
webview and, in the next iteration, manages local SSH tunnels with the user's
SSH agent.

Built with .NET 10 and [Photino.NET](https://www.tryphotino.io/); no browser
runtime is bundled, the OS webview is used (WKWebView, WebView2, WebKitGTK).

## Status

- Multi-instance manager: add, remove and open installations; the list is
  stored per user in `instances.json`.
- One window per open instance, so sessions and cookies stay independent.
- Tunnel manager: sign in with Keycloak through the OAuth 2.0 device grant
  (PKCE S256) or paste a personal token with `tunnels:operate`, load the
  allowlist from the instance and open `ssh -L`/`-J` forwards through the
  user's SSH agent. The refresh token stays in the OS secret store and is used
  to restore the session and to renew an expired access token; access tokens
  live only in memory. `Esci` forgets the stored session. The app shows active
  tunnels and closes them on request or on exit.
- Still to do: signing, notarization and auto-update.

## Run

```bash
dotnet run --project src/InfraOrgIt.Desktop
```

For a local instance with the self-signed certificate:

```bash
INFRAORGIT_INSECURE_TLS=1 dotnet run --project src/InfraOrgIt.Desktop
```

Requires the .NET 10 SDK and the system `ssh` client (OpenSSH). On Linux the
webview needs WebKitGTK (`libwebkit2gtk-4.1-0` on Debian/Ubuntu). Load the key
in the agent (`ssh-add`) before opening a tunnel; the app never reads private
keys.

Create the token from the instance user menu (`/tokens`) with
`tunnels:operate`; the same token needs the `ServerOperator` or `ServerAdmin`
role on the environments to expose.

`Accedi con Keycloak` uses the public `infraorgit-mcp-desktop` client with the
device authorization grant, PKCE S256 and the scopes
`openid profile email mcp:read tunnels:operate`. The panel
shows the user code; the verification page opens in the default browser and
the allowlist loads as soon as the token arrives. The manual token form stays
available as a fallback. After the first sign-in the panel restores the saved
session automatically and shows `Sessione salvata`.

### Session storage

Only the refresh token is persisted, keyed by instance URL:

- macOS: Keychain generic password, service `InfraOrgIT Desktop`, account the
  instance URL, through `/usr/bin/security`.
- Windows: Credential Manager generic credential named
  `InfraOrgIT:<instance URL>`, through `advapi32`.
- Linux: Secret Service through `secret-tool`, label `InfraOrgIT Desktop`,
  service `infraorgit`, account the instance URL. It needs a desktop session
  with D-Bus; headless sessions fall back to memory.

If the platform tool is missing or refuses access, the app keeps the session
in memory for the current run and shows a non-blocking warning. Access tokens
are never written to disk, and refresh tokens never appear in
`instances.json` or logs.

The access token is renewed when it has less than 60 seconds left, and once
more after a `401` from the instance API. If Keycloak rejects the refresh
grant (`invalid_grant`, expired or revoked), the stored session is deleted and
the panel asks `Accedi di nuovo`; `Esci` deletes it on request. The refresh
token lives as long as the Keycloak SSO session: the desktop client has no
`offline_access` scope, so after a long idle time a new sign-in is required.

Instances file:

- macOS: `~/Library/Application Support/InfraOrgIT/instances.json`
- Windows: `%APPDATA%\InfraOrgIT\instances.json`
- Linux: `~/.config/infraorgit/instances.json`

## Test

```bash
dotnet test InfraOrgITDesktop.slnx
```

## Package

```bash
scripts/publish.sh osx-arm64   # or osx-x64, linux-x64, win-x64
```

Self-contained publish. On macOS the script builds `InfraOrgIT Desktop.app`
plus a zip; set `CODESIGN_IDENTITY` to sign it. Linux produces a `.tar.gz` and
Windows a `.zip` of the published folder. Set `VERSION` to change the default
`0.1.0` in the artifact names.

`.github/workflows/publish.yml` packages all four RIDs as unsigned artifacts.
Run it manually (`version` input, default `0.1.0`) or push a `v*` tag (the tag
is used with the leading `v` stripped):

- `InfraOrgIT-Desktop-<version>-osx-arm64` (`macos-latest`)
- `InfraOrgIT-Desktop-<version>-osx-x64` (`macos-15-intel`)
- `InfraOrgIT-Desktop-<version>-win-x64` (`windows-latest`)
- `InfraOrgIT-Desktop-<version>-linux-x64` (`ubuntu-latest`)

Each artifact holds the zip or tar.gz from `scripts/publish.sh`. Signing
(macOS Developer ID and Windows certificate), notarization and auto-update are
still manual; the workflow has no signing secrets and does not create releases.

## Layout

- `src/InfraOrgIt.Desktop/Program.cs`: manager window, message bridge, child
  windows.
- `src/InfraOrgIt.Desktop/Auth/`: Keycloak device grant, PKCE, refresh and the
  OS token stores (Keychain, Credential Manager, secret-tool).
- `src/InfraOrgIt.Desktop/Instances/`: instance model and JSON store.
- `src/InfraOrgIt.Desktop/Tunnels/`: allowlist client, ssh argument builder,
  port allocation and process tracking.
- `src/InfraOrgIt.Desktop/ui/`: local manager page (Italian copy).

## Decisions

See `../AllInfraOrgIT/docs/adr/010-mcp-server-and-tunnel-app.md`: tunnels live
in the desktop app, use the system SSH agent and only open allowlisted targets.
