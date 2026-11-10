using System.Collections.Concurrent;

namespace InfraOrgIt.Desktop.Auth;

/// <summary>
/// Stores the refresh token of a signed-in instance, keyed by instance URL.
/// Only the refresh token is written: access tokens stay in memory. A store
/// never logs or returns tokens through error messages.
/// </summary>
public interface ITokenStore
{
    /// <summary>True when tokens survive an app restart.</summary>
    bool IsPersistent { get; }

    /// <summary>Short backend name shown in the UI.</summary>
    string Backend { get; }

    Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string instanceUrl,
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Failure of a platform secret store. Messages never contain the token or
/// the output of the platform tool.
/// </summary>
public sealed class TokenStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

internal static class TokenStoreKey
{
    public static string Normalize(string instanceUrl) =>
        instanceUrl.Trim().TrimEnd('/');
}

/// <summary>
/// In-memory store used by tests and as the fallback when the platform store
/// is unavailable. Tokens are lost when the process exits.
/// </summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<string, string> _tokens =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsPersistent => false;

    public string Backend => "memoria";

    public Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.GetValueOrDefault(
            TokenStoreKey.Normalize(instanceUrl)));

    public Task SaveAsync(
        string instanceUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Token vuoto.", nameof(refreshToken));

        _tokens[TokenStoreKey.Normalize(instanceUrl)] = refreshToken;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        _tokens.TryRemove(TokenStoreKey.Normalize(instanceUrl), out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Uses the platform store when it works and degrades to
/// <see cref="MemoryTokenStore"/> on the first failure. The warning callback
/// runs at most once, so a locked keychain or a missing D-Bus session cannot
/// flood the UI.
/// </summary>
public sealed class FallbackTokenStore(
    ITokenStore primary,
    Action<string>? notifyWarning = null) : ITokenStore
{
    private readonly MemoryTokenStore _fallback = new();
    private readonly Lock _gate = new();
    private bool _degraded;

    public bool IsPersistent
    {
        get
        {
            lock (_gate)
                return !_degraded && primary.IsPersistent;
        }
    }

    public string Backend
    {
        get
        {
            lock (_gate)
                return _degraded ? _fallback.Backend : primary.Backend;
        }
    }

    public async Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        if (IsDegraded)
            return await _fallback.LoadAsync(instanceUrl, cancellationToken);

        try
        {
            return await primary.LoadAsync(instanceUrl, cancellationToken);
        }
        catch (Exception exception) when (ShouldDegrade(exception))
        {
            Degrade(exception);
            return await _fallback.LoadAsync(instanceUrl, cancellationToken);
        }
    }

    public async Task SaveAsync(
        string instanceUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (IsDegraded)
        {
            await _fallback.SaveAsync(instanceUrl, refreshToken, cancellationToken);
            return;
        }

        try
        {
            await primary.SaveAsync(instanceUrl, refreshToken, cancellationToken);
        }
        catch (Exception exception) when (ShouldDegrade(exception))
        {
            Degrade(exception);
            await _fallback.SaveAsync(instanceUrl, refreshToken, cancellationToken);
        }
    }

    public async Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        await _fallback.DeleteAsync(instanceUrl, cancellationToken);

        // Always try the platform store too: a degraded save must not leave an
        // older refresh token behind after logout.
        try
        {
            await primary.DeleteAsync(instanceUrl, cancellationToken);
        }
        catch (Exception exception) when (ShouldDegrade(exception))
        {
            Degrade(exception);
        }
    }

    private bool IsDegraded
    {
        get
        {
            lock (_gate)
                return _degraded;
        }
    }

    private void Degrade(Exception exception)
    {
        lock (_gate)
        {
            if (_degraded)
                return;
            _degraded = true;
        }

        var reason = exception is TokenStoreException
            ? "archivio di sistema non disponibile"
            : "archivio di sistema non accessibile";
        try
        {
            notifyWarning?.Invoke(
                $"Impossibile usare {primary.Backend} ({reason}). " +
                "La sessione resta valida solo per questa esecuzione.");
        }
        catch (Exception)
        {
            // The window may be closing; a warning must never break the store.
        }
    }

    private static bool ShouldDegrade(Exception exception) =>
        exception is not OperationCanceledException;
}

/// <summary>
/// Picks the native store for the current OS. Construction is cheap; the
/// platform tool is probed on the first operation and failures degrade to
/// memory through <see cref="FallbackTokenStore"/>.
/// </summary>
public static class TokenStoreFactory
{
    public static ITokenStore Create(Action<string>? notifyWarning = null)
    {
        ITokenStore? store = null;
        if (OperatingSystem.IsMacOS())
            store = new MacTokenStore(new ProcessRunner());
        else if (OperatingSystem.IsWindows())
            store = new WindowsTokenStore();
        else if (OperatingSystem.IsLinux())
            store = new LinuxTokenStore(new ProcessRunner());

        return store is null
            ? new MemoryTokenStore()
            : new FallbackTokenStore(store, notifyWarning);
    }
}
