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
- Tunnel manager: paste a personal token with `tunnels:operate`, load the
  allowlist from the instance and open `ssh -L`/`-J` forwards through the
  user's SSH agent. The token stays in memory, never on disk; the app shows
  active tunnels and closes them on request or on exit.
- Still to do: packaging, signing and auto-update.

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

Instances file:

- macOS: `~/Library/Application Support/InfraOrgIT/instances.json`
- Windows: `%APPDATA%\InfraOrgIT\instances.json`
- Linux: `~/.config/infraorgit/instances.json`

## Test

```bash
dotnet test InfraOrgITDesktop.slnx
```

## Layout

- `src/InfraOrgIt.Desktop/Program.cs`: manager window, message bridge, child
  windows.
- `src/InfraOrgIt.Desktop/Instances/`: instance model and JSON store.
- `src/InfraOrgIt.Desktop/Tunnels/`: allowlist client, ssh argument builder,
  port allocation and process tracking.
- `src/InfraOrgIt.Desktop/ui/`: local manager page (Italian copy).

## Decisions

See `../AllInfraOrgIT/docs/adr/010-mcp-server-and-tunnel-app.md`: tunnels live
in the desktop app, use the system SSH agent and only open allowlisted targets.
