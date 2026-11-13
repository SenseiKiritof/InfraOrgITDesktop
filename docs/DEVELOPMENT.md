# InfraOrgIT Desktop — development

Technical notes for the desktop app. The user-facing overview is in
[`../README.md`](../README.md).

Desktop shell for InfraOrgIT: opens one or more instances with the system
webview and manages local SSH tunnels with the user's SSH agent.

Built with .NET 10 and [Photino.NET](https://www.tryphotino.io/); no browser
runtime is bundled, the OS webview is used (WKWebView, WebView2, WebKitGTK).

## Electron rework

`prototype-electron/` is the production-intent Electron shell on the
`feat/electron-rework` branch. It is not packaged or released; the Photino app
above remains the shipped desktop until the rework is accepted. See
[`../prototype-electron/README.md`](../prototype-electron/README.md) for the
user-facing notes and screenshots.

- One main window: a local control-room manager (landing, instance tabs,
  command palette, tunnel drawer) plus one `WebContentsView` per instance.
  Each URL uses `persist:instance-<hash>`, so cookies and the BFF login are
  isolated. `Apri all’avvio` persists in `instances.json`; the landing page is
  always one click away with **Istanze**.
- Silent auth: OAuth device flow with `infraorgit-mcp-desktop` and scopes
  `openid profile email offline_access mcp:read tunnels:operate`; only the
  refresh token is persisted (`safeStorage`, `desktop-auth.bin`). Startup
  refreshes Keycloak and calls `POST /auth/desktop-session` with the instance
  partition's `session.fetch`; failure falls back to `/auth/login`. While an
  instance tab is open the same exchange runs every six hours and after system
  resume, extending the same seven-day BFF session; a revoked or expired
  refresh token clears the local session and falls back to login. Renderers
  never receive access, refresh or CSRF tokens.
- Theme: the desktop shell follows the account theme returned by
  `GET /api/session`; it has no theme control of its own. The Web app's theme
  toggle is the only control and cycles light, dark and system; it writes the
  account preference and sends `theme.changed` to the shell, which applies the
  same resolved appearance. `settings.json` is only the fallback before an
  instance loads.
- Tunnels: the manager owns the SSH processes; the Web `/tunnels` panel stays
  the per-instance detail. The drawer groups every instance by server, shows
  the local port and active/stopped state, and enforces a duplicate local-port
  guard before spawning `ssh`. The manager posts start/stop/error and a
  30-second heartbeat to the instance, so the central `/tunnels` table shows
  Electron tunnels too.
- Server anagrafica: the manage dialog lists servers through the instance BFF
  session and opens `/servers/{id}`; the Tunnel services form remains in the
  Web app.

Commands:

```bash
cd prototype-electron
npm install
npm start
npm run test:unit    # fake-fetch device/session, stores, tunnel rules
npm test             # unit + Electron shell test against https://localhost:8443
npm run tunnel-test  # fake SSH arguments and lifecycle
```

`npm test` needs the local full stack (`make full`) and Playwright is not
involved. `DESKTOP_USER_DATA=/tmp/dir` isolates all app files;
`DESKTOP_ALLOW_INSECURE_LOCALHOST=1` is the explicit development override for
the self-signed Caddy certificate in a packaged build. The running stack may
not yet serve `/auth/desktop-session`: the shell then exercises the login
fallback, while fake-fetch tests cover the session exchange. Native
Keychain/Credential Manager glue is the next hardening step beyond
`safeStorage`.

## Status

- Multi-instance manager: add, remove and open installations; the list is
  stored per user in `instances.json`.
- One window per open instance, so navigation and session state stay
  independent. On Windows each child gets its own webview data folder; on
  macOS and Linux Photino 4.0.16 shares the webview data store, so two
  instances on the same host can share the login cookie.
- Tunnel manager: sign in with Keycloak through the OAuth 2.0 device grant
  (PKCE S256) or paste a personal token with `tunnels:operate`, load the
  allowlist from the instance and open `ssh -L`, `-D` or `-R` forwards through
  the user's SSH agent. Multi-hop `-J` chains come from the API nearest-first.
  Each server card lists its deployments and custom services (local, SOCKS and
  remote modes); `Avvia` opens a service by id. Local HTTP/HTTPS services get
  an `Apri` button only while the tunnel is active, and services marked
  `autoStart` start once per allowlist load unless the panel toggle is off.
  The refresh token stays in the OS secret store and is used to restore the
  session and to renew an expired access token; access tokens live only in
  memory. `Esci` forgets the stored session. The app shows active tunnels,
  publishes their live session projection (mode, service, target and port) to
  the instance while they run, and closes them on request or on exit.
  An `autoStart` service whose ssh process exits unexpectedly is reconnected
  automatically: up to three attempts five seconds apart, reusing the same
  service and local port. A port that was auto-allocated is replaced when it
  is no longer free; a busy fixed port stops the retries with an error in the
  panel. A manual stop, a tunnel panel change or app exit cancels pending
  retries. Each attempt is a new tunnel session with its own correlation id.
- Tunnel persistence: every successful start is saved by identity (instance
  URL, server id, custom service or deployment id, mode and requested local
  port) in `tunnels.json` next to `instances.json`. No token, host, username
  or secret is written. After the manager page loads, the app restores the
  saved tunnels with the stored session, without opening a browser; when the
  session is missing or expired it logs one line and keeps the identity so a
  later sign-in can retry. Opening a tunnel panel reconciles the saved list
  with the allowlist (deleted or disabled services are dropped) and never
  starts a tunnel twice; `autoStart` services keep starting once per allowlist
  load. A user stop or `Esci` forgets the identity, an app exit keeps it for
  the next start, and `Non ripristinare` clears the saved list.
- Still to do for tunnels: the restore pass is best effort, so an instance
  that is offline at startup is retried only on the next panel open or app
  start.
- Update check: reads the latest GitHub release, compares its semver tag with
  the assembly informational version and shows the current version. When a
  newer stable release exists, `Apri release` opens its page in the default
  browser and `Scarica aggiornamento` downloads the archive for the current
  RID, verifies the RSA/SHA-256 signature of the published checksum manifest
  with the public key embedded in the app and checks the archive hash against
  that signed manifest. After a verified download the card offers
  `Installa e riavvia` when the app can self-replace (macOS bundle, published
  Linux folder or published Windows folder) or `Apri installer` otherwise.
  Installation starts only after that explicit click and only for an archive
  whose signature and hash were verified; nothing is installed automatically.
  An unsigned release can still be downloaded after an explicit confirmation,
  but automatic installation stays blocked and the download folder opens for a
  manual install.
- Still to do: the updater does not inspect the platform code signature of the
  downloaded app; macOS notarization and Windows Authenticode signing are
  produced by `publish.yml` when the secrets exist.

## Run

```bash
dotnet run --project src/InfraOrgIt.Desktop
```

For a local instance with the self-signed certificate:

```bash
INFRAORGIT_INSECURE_TLS=1 dotnet run --project src/InfraOrgIt.Desktop
```

The flag now applies both to the tunnel `HttpClient` and to the instance
webview. It disables certificate validation for every instance opened in that
run, so do not use it in a packaged install or on a shared host.

The fullscreen API in the macOS child window is left at the Photino
default. Forcing `elementFullscreenEnabled` through
`SetBrowserControlInitParameters` made child windows stop navigating in
Photino 4.0.16, so the desktop does not enable it.

### Desktop download modes

The manager stores `downloadMode` in `settings.json` (`auto`, `bridge`,
`native`). The SPA asks the native process for its capabilities; the server
advertises whether signed ticket URLs are enabled.

- `bridge`: `POST /api/desktop/downloads/tickets`, then the native
  `DownloadManager` streams the ticket URL to a temporary `.part` file and
  renames it. Downloads run in background tasks with at most two concurrent
  slots.
- `native`: Windows WebView2 or the patched macOS webview saves the
  attachment. On macOS it needs the patched `Photino.Native` built from
  `native/photino-download`; the app registers it through
  `NativeLibrary.SetDllImportResolver` only when the dylib is next to the
  executable. The patch is opt-in (`PHOTINO_MAC_DOWNLOADS=1`) and does not
  cover blob URLs, so API results keep using the managed bridge.
- `auto`: native when the patched library was registered, otherwise bridge.

`publish.yml` builds the native dylib on macOS, `scripts/publish.sh` copies it
to `native/` in the app bundle and signs it with the same Developer ID before
the app signature. A local build without the dylib still advertises the bridge;
run `native/photino-download/build.sh` to build it.

### Instance URLs

Instance URLs use `https` for every host. Plain `http` is accepted only for
loopback hosts (`localhost`, `127.0.0.1`, `[::1]`), so a local dev stack keeps
working. A remote `http` URL is rejected with an Italian message; saved remote
`http` entries are skipped when `instances.json` is loaded and must be re-added
as `https`.

`INFRAORGIT_ALLOW_INSECURE_HTTP=1` is the development-only escape hatch that
allows a remote plain-http instance. Treat it like `INFRAORGIT_INSECURE_TLS`:
never set it in a packaged install or on a shared host, because bearer tokens
and inventory data would travel in cleartext.

```bash
INFRAORGIT_ALLOW_INSECURE_HTTP=1 dotnet run --project src/InfraOrgIt.Desktop
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
  instance URL, through the native Security.framework API
  (`SecKeychainAdd/FindGenericPassword`). The token is passed in memory, never
  in the arguments of a helper process or in `argv`.
- Windows: Credential Manager generic credential named
  `InfraOrgIT:<instance URL>`, through `advapi32`.
- Linux: Secret Service through `secret-tool`, label `InfraOrgIT Desktop`,
  service `infraorgit`, account the instance URL. It needs a desktop session
  with D-Bus; headless sessions fall back to memory.

If the platform store is missing or refuses access, the app keeps the session
in memory for the current run and shows a non-blocking warning. Access tokens
are never written to disk, and refresh tokens never appear in
`instances.json`, logs or a child process argument list.

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

Saved tunnels use `tunnels.json` in the same directory:

- macOS: `~/Library/Application Support/InfraOrgIT/tunnels.json`
- Windows: `%APPDATA%\InfraOrgIT\tunnels.json`
- Linux: `~/.config/infraorgit/tunnels.json`

## Updates

The `Aggiornamenti` card reads the latest stable GitHub release and lists its
release notes. When a newer version exists, `Scarica aggiornamento` downloads
the archive for the current runtime identifier (`osx-arm64`, `osx-x64`,
`win-x64` or `linux-x64`):

- The release API asset list is authoritative. The exact RID wins; when it is
  missing, the same OS with another architecture is accepted as the documented
  fallback, using the platform archive format: macOS and Windows `.zip`, Linux
  `.tar.gz`.
- The archive URL must be `https://github.com/...`; anything else is rejected
  before a request. GitHub redirects the download to its object storage, which
  `HttpClient` follows internally.
- The release publishes a signed checksum manifest,
  `InfraOrgIT-Desktop-<version>-sha256.txt`, and its detached signature
  `...-sha256.txt.sig`. The app downloads both, verifies the RSA/SHA-256
  signature with the public key embedded in the binary and only then reads the
  expected hash for the archive. A SHA-256 file published next to an unsigned
  release is never trusted on its own.
- The archive must be listed in the signed manifest and its SHA-256 must match
  byte for byte. The app downloads to `<archive>.part`, compares the hash and
  only then renames it; a mismatch deletes the file and reports the error in
  Italian.
- When the manifest or the signature is missing, invalid, not downloadable or
  does not list the archive, the UI shows a warning and requires the explicit
  `Scarica senza firma` confirmation. The archive can still be downloaded for a
  manual install, but `Installa e riavvia` stays blocked and the card offers
  `Apri installer` only.
- Files go to `~/Downloads/InfraOrgIT Desktop`, or a temporary folder when the
  home folder is unknown. After a successful download the app opens the
  containing folder and shows the full path. The download itself never
  executes the archive; installation starts only from the explicit install
  action.
- Network, HTTP, signature and checksum failures come back as UI messages
  instead of exceptions. Tests use a fake `HttpMessageHandler`, so no test
  touches the network.

### Installation

The download result carries the archive path and `SignatureVerified`. The
install button appears only after a successful download; `Installa e riavvia`
requires `SignatureVerified=true`, otherwise the action opens the download
folder. The backend re-checks that flag when it receives `update.install`, so a
missing signature or a hash mismatch can never start an install, even if the UI
is bypassed. The refusal message is
`Installazione bloccata: firma aggiornamento non valida/mancante.`

- macOS: when the process runs from a `.app` (`AppContext.BaseDirectory`
  contains `Contents/MacOS`), `Installa e riavvia` unpacks the zip with
  `ditto -x -k` in a fresh temp directory, requires exactly one `*.app`, and
  checks `Contents/Info.plist` for
  `CFBundleIdentifier=it.infraorgit.desktop`. A detached `sh` helper receives
  every path as an argument (no shell-string interpolation), waits for the app
  PID, moves the old bundle aside, copies the new one with `ditto`, removes
  `com.apple.quarantine`, relaunches with `open`, and restores the old bundle
  if the copy fails. The app closes after the helper starts. A `dotnet run`
  build, an invalid archive or a non-writable install folder is refused with
  an Italian message and the download folder opens for a manual install.
- Windows: for a published folder (apphost `InfraOrgIt.Desktop.exe`, no
  managed `.dll` next to it), `Installa e riavvia` starts a detached
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` helper. The
  installer reads the zip directory and requires exactly one top-level folder
  with `InfraOrgIt.Desktop.exe` directly inside it; rooted or `..` entries are
  refused. The helper waits for the app PID, expands the zip into a fresh temp
  folder, moves the current app folder aside, copies the new payload into
  place, restores the old folder when the copy fails, starts the new exe and
  cleans temp and old folder. Every path is passed as an argument and read
  with `-LiteralPath`; no user-controlled string is written into the script
  body. A `dotnet run` build (managed `.dll` present) or a missing exe is
  refused with an Italian message and `Apri installer` opens the download
  folder. `scripts/self-install-windows.ps1` runs this helper against a real
  publish (replace, PID wait and restore); the `self-install-windows` job in
  `smoke.yml` runs the script on `windows-latest`.
- Linux: for a published folder (apphost binary, no managed `.dll`), the same
  `Installa e riavvia` starts a helper that waits for the PID, extracts the
  tar.gz into a temp directory, moves the current app folder aside, copies the
  new payload with `cp -a`, and execs the new binary; a failed copy restores
  the old folder. A non-writable folder, `dotnet run` or an invalid archive
  falls back to `Apri installer`.
- Helpers write a log next to their temp work folder (`install.log` in
  `$TMPDIR/infraorgit-install-*` on macOS and Linux,
  `%TEMP%\infraorgit-install-<id>.log` on Windows) so a failed replacement can
  be diagnosed. The app only closes after the helper process has started.

`publish.yml` writes a `<archive>.sha256` next to every archive after signing
and notarization. After all four RIDs are packaged, a `Signed checksum manifest`
job hashes the uploaded archives again, writes the deterministic
`InfraOrgIT-Desktop-<version>-sha256.txt` manifest and signs it with
`UPDATE_SIGNING_KEY` into `...-sha256.txt.sig`. The manifest and signature are
uploaded to the workflow artifacts and to the GitHub Release together with the
archives and their per-archive checksums. Without the secret the job logs a
warning and uploads the manifest unsigned; the app then requires a manual
install.

## Test

```bash
dotnet test InfraOrgITDesktop.slnx
```

Update tests generate an ephemeral RSA-3072 test keypair and sign their own
manifest, so the production private key is never present in the repository or
in CI. The embedded production public key is only asserted to parse. The
signature tests cover a valid signature and hash, an invalid signature, a
missing signature, an archive that the signed manifest does not list and a
checksum mismatch; none of them starts an install.

### Platform integration tests

`dotnet test` also exercises the real OS integrations when the host provides
them. Every test reports `Skipped` instead of failing when the platform or the
tool is missing, so the same suite runs unchanged in the `build-test` CI matrix
(`ubuntu-latest`, `macos-latest`, `windows-latest`) without extra steps or
secrets:

| Integration | macOS | Windows | Linux |
| --- | --- | --- | --- |
| Refresh token round-trip | Keychain through native Security.framework | Credential Manager through `advapi32` | `secret-tool` with a D-Bus session |
| Update helper | generated `install.sh` against a disposable `.app` (replace and restore) | generated `install.ps1` against a real published folder (replace, PID wait and restore) through `scripts/self-install-windows.ps1` | generated `install.sh` against a disposable app directory (replace and restore) |

The token tests save, load and delete a throwaway refresh token under a unique
service/account suffix, clean up in a `finally` block and never print the
token. macOS skips when the Keychain is locked or unavailable, Linux skips
without `secret-tool` or `DBUS_SESSION_BUS_ADDRESS`, and Windows only runs on
Windows.

The helper tests run the real generated shell script with a real exited PID
against temporary app paths. The macOS launch step is replaced with a recorder
so the disposable test bundle is never opened; the Linux helper execs the
disposable fake binary. Nothing outside the test directory is touched. The
Windows test also starts the published app from the temporary installation
folder.

### Windows self-install end-to-end

`scripts/self-install-windows.ps1` (PowerShell 7) publishes two real
self-contained `win-x64` builds, `0.1.0` and `0.2.0`, and writes a
`self-install-marker.txt` into each. It keeps the old publish as the
"installation" source, rebuilds the new one with the release layout
(`InfraOrgIT-Desktop-0.2.0-win-x64/...`) and zips it. Two xUnit tests then run
the generated helper:

1. Start the real `InfraOrgIt.Desktop.exe` from the old folder and wait for
   its manager window.
2. Run the real installer in-process to obtain the generated `install.ps1` and
   its argument list, so the script and paths come from production code.
3. Replace only the `Start-Process` launch line with a recorder (the internal
   `CreateWindowsInstallScript` seam), so a second GUI does not start.
4. Assert the helper logged its start and is still running while the app PID is
   alive and the old marker is untouched; close the app; assert exit code 0,
   the new marker and a recorder hit.
5. Repeat with a zip whose payload exe is a text file and no seam: the launch
   step fails after the old folder has been moved aside, and the helper
   restores the old folder, marker and executable.

```powershell
pwsh scripts/self-install-windows.ps1
pwsh scripts/self-install-windows.ps1 -Root C:\temp\self-install -Keep
```

The tests read `INFRAORGIT_SELF_INSTALL_OLD_PUBLISH`, `..._ARCHIVE`,
`..._OLD_MARKER`, `..._NEW_MARKER` and an optional `..._EVIDENCE` file. Without
them `WindowsSelfInstallTests` reports `Skipped`, so `dotnet test` and the
`build-test` matrix are unchanged. The `self-install-windows` job in
`smoke.yml` runs the script on `windows-latest` with `-Keep` and uploads
`evidence.txt`. It needs an interactive desktop session and the WebView2
runtime, like the GUI smoke test. It is a diagnostic job, not one of the
required checks: if a future runner cannot open the window or run
`powershell.exe`, the job stays red with the failing step instead of passing
silently.

### Real published bundle self-install (macOS)

`scripts/test-real-self-install.sh` complements the fake-layout tests above
with two real bundles built by `scripts/publish.sh`: the installed version and
a newer update payload. It publishes both into temporary `DIST_ROOT` folders,
copies the old bundle into a disposable `Applications` folder, and runs
`UpdateInstallerRealBundleTests` against the real generated helper:

- the published zip is extracted with `ditto` and the `.app` identifier is
  checked by `UpdateInstaller`;
- the real app executable is started from the old bundle (a live stand-in is
  used when the host has no GUI session) and the helper must stay alive until
  that PID exits;
- the relaunch is recorded instead of opening a window, and the replaced
  bundle must carry the new version and marker;
- a forced copy failure must leave the old bundle and version in place;
- the helper work directory and the old bundle backup must disappear.

The script is macOS-only, uses the host architecture by default
(`RID=osx-x64` for an Intel build), leaves `dist/` alone, never writes to
`/Applications`, kills the app it starts and removes its temp tree. It is not
part of CI because it publishes two full bundles; run it locally when changing
the macOS install helper:

```bash
scripts/test-real-self-install.sh
```

### GUI smoke tests

The smoke checks build the app, start the manager window and fail if the
process exits before 8 seconds; `SMOKE_HOLD_SECONDS` changes the hold. They
check that the window is created and the event loop stays up, not that the
webview content rendered: the manager page itself is covered by the unit
tests.

Linux needs the WebKitGTK runtime, Xvfb and `xauth`:

```bash
sudo apt-get install libwebkit2gtk-4.1-0 libnotify4 xvfb xauth
dotnet build InfraOrgITDesktop.slnx -warnaserror
scripts/smoke.sh
```

The script starts the window under `xvfb-run -a`. The equivalent manual
command is:

```bash
xvfb-run -a dotnet run --project src/InfraOrgIt.Desktop --no-build
```

On Windows, run the built executable and check it is still alive:

```powershell
dotnet build InfraOrgITDesktop.slnx -warnaserror
$exe = 'src/InfraOrgIt.Desktop/bin/Debug/net10.0/InfraOrgIt.Desktop.exe'
$process = Start-Process $exe -PassThru
Start-Sleep -Seconds 8
if ($process.HasExited) { throw "App exited early with $($process.ExitCode)" }
Stop-Process -Id $process.Id -Force
```

`.github/workflows/smoke.yml` runs the Linux check on `ubuntu-latest`, the
Windows check on `windows-latest` and the Windows self-install end-to-end job
described above, separate from the `build-test` matrix. The Windows runner
starts the app in its desktop session and ships the WebView2 runtime; the job
asserts the process stays up and logs the main window handle for diagnostics. It does not validate the webview content, so a runner that
can create the window but not render the page would still pass. If the GUI
cannot start on a future runner image, mark the Windows job non-blocking and
document the failure instead of leaving a red check. On a machine with a
desktop session no Xvfb is needed: `dotnet run --project
src/InfraOrgIt.Desktop` opens the window normally.

## Package

```bash
scripts/publish.sh osx-arm64   # or osx-x64, linux-x64, win-x64
```

Self-contained publish. On macOS the script builds `InfraOrgIT Desktop.app`
plus a zip; set `CODESIGN_IDENTITY` to sign it with hardened runtime and a
secure timestamp. Linux produces a `.tar.gz` and Windows a `.zip` of the
published folder. Set `VERSION` to change the default `0.1.0` in the artifact
names and in the assembly version used by the update check, and `DIST_ROOT` to
write the build outside `dist/` (the real-bundle self-install test uses it).

`.github/workflows/publish.yml` packages all four RIDs:

- `InfraOrgIT-Desktop-<version>-osx-arm64` (`macos-latest`)
- `InfraOrgIT-Desktop-<version>-osx-x64` (`macos-15-intel`)
- `InfraOrgIT-Desktop-<version>-win-x64` (`windows-latest`)
- `InfraOrgIT-Desktop-<version>-linux-x64` (`ubuntu-latest`)

Run it manually (`version` input, default `0.1.0`) to upload the workflow
artifacts only. Pushing a `v*` tag (created manually from `main`, never by the
workflow) uses the tag without the leading `v` and also creates or updates a
GitHub Release with the same archives, their `.sha256` files and the signed
manifest. Each package artifact holds the zip or tar.gz from
`scripts/publish.sh` plus the checksum generated after any signing or
notarization step; a final `Signed checksum manifest` job adds
`InfraOrgIT-Desktop-<version>-sha256.txt` and, when `UPDATE_SIGNING_KEY` is
configured, its `.sig`.

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
| `UPDATE_SIGNING_KEY` | RSA private key in PEM used to sign the update checksum manifest |

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

### Update manifest key

The update manifest is signed with an RSA-3072 key kept outside the repository.
Only the public key is versioned, at
`src/InfraOrgIt.Desktop/Update/update-public.pem`; the project embeds it as
`InfraOrgIt.Desktop.Update.update-public.pem`. Its SHA-256 fingerprint over the
DER SubjectPublicKeyInfo is
`cdb5ec1e5892cecc119f361ba451c3431a1c486461f2ad64665a5ad386254ca1`.

Generate a workstation key (macOS or Linux; on Windows use WSL):

```bash
mkdir -p ~/.config/infraorgit
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 \
  -out ~/.config/infraorgit/update-signing-key.pem
chmod 600 ~/.config/infraorgit/update-signing-key.pem
openssl pkey -in ~/.config/infraorgit/update-signing-key.pem \
  -pubout -out src/InfraOrgIt.Desktop/Update/update-public.pem
openssl pkey -pubin -in src/InfraOrgIt.Desktop/Update/update-public.pem \
  -outform DER | openssl dgst -sha256
```

Commit only the public key. Store the private key in
`~/.config/infraorgit/update-signing-key.pem` (mode `600`), back it up outside
the repository and configure the `UPDATE_SIGNING_KEY` repository secret with the
whole PEM file:

```bash
gh secret set UPDATE_SIGNING_KEY --repo SenseiKiritof/InfraOrgITDesktop \
  < ~/.config/infraorgit/update-signing-key.pem
```

In GitHub it is Settings -> Secrets and variables -> Actions -> New repository
secret, name `UPDATE_SIGNING_KEY`, value the full PEM text. Never print, paste
or commit the private key; `publish.yml` writes it to `$RUNNER_TEMP` with
`umask 077`, signs the manifest, verifies the signature with the derived public
key and deletes the file. Rotating the key needs a release that ships the new
public key first, because clients only trust the key embedded in the copy they
already run.

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
  port allocation, process tracking and the saved tunnel state.
- `src/InfraOrgIt.Desktop/Update/`: GitHub release check, semver comparison,
  signed checksum manifest verification (`UpdateSignature`), archive download,
  the embedded public key and the verified install helpers (macOS, Windows and
  Linux).
- `src/InfraOrgIt.Desktop/ui/`: local manager page (Italian copy).

## Decisions

See `../AllInfraOrgIT/docs/adr/010-mcp-server-and-tunnel-app.md`: tunnels live
in the desktop app, use the system SSH agent and only open allowlisted targets.
