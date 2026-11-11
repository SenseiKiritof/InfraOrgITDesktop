namespace InfraOrgIt.Desktop.Auth;

/// <summary>
/// Linux Secret Service through <c>secret-tool</c>. A missing tool or a
/// session without D-Bus fails the first operation and the caller falls back
/// to memory.
/// </summary>
public sealed class LinuxTokenStore : ITokenStore
{
    internal const string Tool = "secret-tool";
    internal const string Label = "InfraOrgIT Desktop";
    private const string Service = "infraorgit";
    private const int NotFound = 1;

    private readonly IProcessRunner _runner;
    private readonly string _service;

    public LinuxTokenStore(IProcessRunner runner)
        : this(runner, Service)
    {
    }

    /// <summary>
    /// Test seam: a unique service keeps a test item away from the real
    /// instance entries.
    /// </summary>
    internal LinuxTokenStore(IProcessRunner runner, string service)
    {
        _runner = runner;
        _service = service;
    }

    public bool IsPersistent => true;

    public string Backend => "secret-tool";

    public async Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            Tool,
            ["lookup", "service", _service, "account", TokenStoreKey.Normalize(instanceUrl)],
            null,
            cancellationToken);

        if (result.ExitCode == NotFound)
            return null;
        if (result.ExitCode != 0)
        {
            throw new TokenStoreException(
                $"Lettura da secret-tool non riuscita (codice {result.ExitCode}).");
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

        var result = await _runner.RunAsync(
            Tool,
            [
                "store",
                "--label=" + Label,
                "service", _service,
                "account", TokenStoreKey.Normalize(instanceUrl)
            ],
            refreshToken,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new TokenStoreException(
                $"Salvataggio con secret-tool non riuscito (codice {result.ExitCode}).");
        }
    }

    public async Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            Tool,
            ["clear", "service", _service, "account", TokenStoreKey.Normalize(instanceUrl)],
            null,
            cancellationToken);

        if (result.ExitCode is not (0 or NotFound))
        {
            throw new TokenStoreException(
                $"Rimozione con secret-tool non riuscita (codice {result.ExitCode}).");
        }
    }
}
