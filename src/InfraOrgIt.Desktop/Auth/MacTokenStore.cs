namespace InfraOrgIt.Desktop.Auth;

/// <summary>
/// macOS Keychain through <c>/usr/bin/security</c>. The refresh token is the
/// generic-password value; the instance URL is the account name.
/// </summary>
public sealed class MacTokenStore(IProcessRunner runner) : ITokenStore
{
    internal const string Service = "InfraOrgIT Desktop";
    private const string SecurityPath = "/usr/bin/security";
    private const int ItemNotFound = 44;

    public bool IsPersistent => true;

    public string Backend => "Portachiavi";

    public async Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            SecurityPath,
            [
                "find-generic-password",
                "-s", Service,
                "-a", TokenStoreKey.Normalize(instanceUrl),
                "-w"
            ],
            null,
            cancellationToken);

        if (result.ExitCode == ItemNotFound)
            return null;
        if (result.ExitCode != 0)
        {
            throw new TokenStoreException(
                $"Lettura dal Portachiavi non riuscita (codice {result.ExitCode}).");
        }

        var token = result.StandardOutput.Trim();
        return token.Length == 0 ? null : token;
    }

    public async Task SaveAsync(
        string instanceUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Token vuoto.", nameof(refreshToken));

        var result = await runner.RunAsync(
            SecurityPath,
            [
                "add-generic-password",
                "-U",
                "-s", Service,
                "-a", TokenStoreKey.Normalize(instanceUrl),
                "-w", refreshToken
            ],
            null,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new TokenStoreException(
                $"Salvataggio nel Portachiavi non riuscito (codice {result.ExitCode}).");
        }
    }

    public async Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            SecurityPath,
            [
                "delete-generic-password",
                "-s", Service,
                "-a", TokenStoreKey.Normalize(instanceUrl)
            ],
            null,
            cancellationToken);

        if (result.ExitCode is not (0 or ItemNotFound))
        {
            throw new TokenStoreException(
                $"Rimozione dal Portachiavi non riuscita (codice {result.ExitCode}).");
        }
    }
}
