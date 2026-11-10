# InfraOrgITDesktop — agent guide

Desktop shell for InfraOrgIT (.NET 10 + Photino.NET). Sibling of
`../InfraOrgITApi`, `../InfraOrgITWeb`, `../InfraOrgITDocker` and
`../AllInfraOrgIT`.

## Commands

- `dotnet run --project src/InfraOrgIt.Desktop`: start the manager window.
- `dotnet test InfraOrgITDesktop.slnx`: unit tests.
- `dotnet build InfraOrgITDesktop.slnx -warnaserror`: the build gate.
- `scripts/publish.sh [RID]`: self-contained publish plus a zip/tar.gz in
  `dist/`; macOS also builds the `.app` (optional `CODESIGN_IDENTITY`).

## Rules

- UI copy is Italian; code identifiers and commit messages are English
  (Conventional Commits).
- The app never stores passwords or private keys. Tunnels use the user's
  `ssh`/agent and only allowlisted targets returned by the instance API.
- Personal and device-flow access tokens live only in memory
  (`TunnelManager`). The refresh token is stored through `Auth/ITokenStore`,
  keyed by instance URL: Keychain on macOS, Credential Manager on Windows,
  `secret-tool` on Linux. Never write tokens to `instances.json` or logs. If
  the platform store fails, `FallbackTokenStore` keeps the session in memory
  and warns in the UI.
- `Instances/InstanceStore` is the only file persistence: keep saves atomic
  and validate URLs (`http`/`https` only).
- `Tunnels/SshTunnel` builds `ArgumentList`, never a shell string; keep
  `BatchMode` and host-key verification on.
- `Tunnels/TunnelManager` owns tokens, targets and processes; UI messages are
  JSON through the manager bridge.
- Photino has a single native message loop: create child windows from the
  manager's web-message handler (the existing pattern), never from a
  background thread.
- Manager page is local HTML with its own CSS/JS; no external CDN.
- Keep `README.md` status and the roadmap in the meta repository aligned.
