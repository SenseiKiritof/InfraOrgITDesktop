# InfraOrgITDesktop — agent guide

Desktop shell for InfraOrgIT (.NET 10 + Photino.NET). Sibling of
`../InfraOrgITApi`, `../InfraOrgITWeb`, `../InfraOrgITDocker` and
`../AllInfraOrgIT`.

## Commands

- `dotnet run --project src/InfraOrgIt.Desktop`: start the manager window.
- `dotnet test InfraOrgITDesktop.slnx`: unit tests.
- `dotnet build InfraOrgITDesktop.slnx -warnaserror`: the build gate.

## Rules

- UI copy is Italian; code identifiers and commit messages are English
  (Conventional Commits).
- The app never stores passwords or private keys. Tunnels use the user's
  `ssh`/agent and only allowlisted targets returned by the instance API.
- `Instances/InstanceStore` is the only persistence: keep saves atomic and
  validate URLs (`http`/`https` only).
- Photino has a single native message loop: create child windows from the
  manager's web-message handler (the existing pattern), never from a
  background thread.
- Manager page is local HTML with its own CSS/JS; no external CDN.
- Keep `README.md` status and the roadmap in the meta repository aligned.
