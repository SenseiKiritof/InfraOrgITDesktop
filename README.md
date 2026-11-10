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
- Still to do: auto-update.

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
plus a zip; set `CODESIGN_IDENTITY` to sign it with hardened runtime and a
secure timestamp. Linux produces a `.tar.gz` and Windows a `.zip` of the
published folder. Set `VERSION` to change the default `0.1.0` in the artifact
names.

`.github/workflows/publish.yml` packages all four RIDs:

- `InfraOrgIT-Desktop-<version>-osx-arm64` (`macos-latest`)
- `InfraOrgIT-Desktop-<version>-osx-x64` (`macos-15-intel`)
- `InfraOrgIT-Desktop-<version>-win-x64` (`windows-latest`)
- `InfraOrgIT-Desktop-<version>-linux-x64` (`ubuntu-latest`)

Run it manually (`version` input, default `0.1.0`) to upload the workflow
artifacts only. Pushing a `v*` tag (created manually from `main`, never by the
workflow) uses the tag without the leading `v` and also creates or updates a
GitHub Release with the same archives. Each artifact holds the zip or tar.gz
from `scripts/publish.sh`.

### Signing and notarization

Signing is optional and driven by repository secrets. When they are missing,
the workflow logs a warning and publishes the unsigned artifact, so manual runs
and forks keep working.

| Secret | Used for |
| --- | --- |
| `MACOS_CERTIFICATE` | Base64 `.p12` with the Developer ID Application certificate and private key |
| `MACOS_CERTIFICATE_PASSWORD` | Password of that `.p12` |
| `APPLE_ID` | Apple ID passed to `notarytool` |
| `APPLE_TEAM_ID` | Apple Developer team ID passed to `notarytool` |
| `APPLE_APP_PASSWORD` | App-specific password for `notarytool` (appleid.apple.com) |
| `WINDOWS_CERTIFICATE` | Base64 `.pfx` with the Authenticode code-signing certificate and private key |
| `WINDOWS_CERTIFICATE_PASSWORD` | Password of that `.pfx` |

macOS needs all five macOS/Apple secrets; Windows needs both `.pfx` secrets.
Encode the files with `base64 -i certificate.p12 | pbcopy` on macOS or
`[Convert]::ToBase64String([IO.File]::ReadAllBytes("certificate.pfx"))` in
PowerShell. Never commit the certificates or their passwords.

With the secrets configured:

- macOS imports the certificate into a temporary keychain, derives the
  `Developer ID Application` identity, passes it to `scripts/publish.sh` as
  `CODESIGN_IDENTITY`, submits the zip with `xcrun notarytool submit --wait`,
  staples the ticket and rebuilds the zip so the archive ships notarized.
- Windows unpacks the archive, signs the executable with `signtool` (SHA-256
  and timestamp), verifies the signature and rebuilds the zip. No MSIX is
  produced.
- Linux stays unsigned.

The `contents: write` permission is granted only to the release job.

### Manual release

1. Merge the change through a pull request into `dev`, then `dev` into `main`,
   and wait for the `build-test` checks.
2. From `main`, create and push the tag by hand:
   `git tag -a v0.2.0 -m "InfraOrgIT Desktop 0.2.0" && git push origin v0.2.0`.
3. The `publish` workflow builds all RIDs and creates or updates the
   `v0.2.0` GitHub Release. Re-running the workflow for the same tag updates
   the release instead of creating a new tag.
4. Without secrets, archive signing and notarization are still manual:
   `CODESIGN_IDENTITY="Developer ID Application: ..." VERSION=0.2.0 scripts/publish.sh osx-arm64`,
   then `notarytool submit`, `stapler staple` and `ditto` as described above.

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
