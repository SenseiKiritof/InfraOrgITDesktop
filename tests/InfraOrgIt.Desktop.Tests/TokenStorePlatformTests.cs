using InfraOrgIt.Desktop.Auth;

namespace InfraOrgIt.Desktop.Tests;

/// <summary>
/// Round-trips a throwaway refresh token through the real platform secret
/// store. Each test uses a unique service/account suffix, deletes the item in
/// a finally block and never prints the token. Hosts without the platform
/// tool, an unlocked keychain or a D-Bus session report Skipped.
/// </summary>
public sealed class TokenStorePlatformTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task Mac_keychain_round_trips_a_refresh_token()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "The macOS Keychain store only runs on macOS.");
        Skip.IfNot(
            File.Exists("/usr/bin/security"),
            "/usr/bin/security is not available.");

        var service = $"{MacTokenStore.Service} Tests {Guid.NewGuid():N}";
        var account = $"https://keychain-test-{Guid.NewGuid():N}.invalid";
        var token = "refresh-" + Guid.NewGuid().ToString("N");
        var store = new MacTokenStore(new ProcessRunner(), service);
        using var timeout = new CancellationTokenSource(Timeout);

        try
        {
            try
            {
                await store.SaveAsync(account, token, timeout.Token);
            }
            catch (TokenStoreException exception)
            {
                Skip.If(
                    true,
                    "The Keychain is not available in this session: " +
                    exception.Message);
            }
            catch (OperationCanceledException)
            {
                Skip.If(true, "The Keychain did not answer in time.");
            }

            var loaded = await store.LoadAsync(account, timeout.Token);
            Assert.True(
                string.Equals(token, loaded, StringComparison.Ordinal),
                "The Keychain did not return the saved refresh token.");

            await store.DeleteAsync(account, timeout.Token);
            Assert.Null(await store.LoadAsync(account, timeout.Token));
        }
        finally
        {
            await TryDeleteAsync(store, account);
        }
    }

    [SkippableFact]
    public async Task Windows_credential_manager_round_trips_a_refresh_token()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The Credential Manager store only runs on Windows.");

        var prefix = $"InfraOrgIT-Tests:{Guid.NewGuid():N}:";
        var account = $"https://credential-test-{Guid.NewGuid():N}.invalid";
        var token = "refresh-" + Guid.NewGuid().ToString("N");
        var store = new WindowsTokenStore(prefix);
        using var timeout = new CancellationTokenSource(Timeout);

        try
        {
            await store.SaveAsync(account, token, timeout.Token);
            var loaded = await store.LoadAsync(account, timeout.Token);
            Assert.True(
                string.Equals(token, loaded, StringComparison.Ordinal),
                "Credential Manager did not return the saved refresh token.");

            await store.DeleteAsync(account, timeout.Token);
            Assert.Null(await store.LoadAsync(account, timeout.Token));
        }
        finally
        {
            await TryDeleteAsync(store, account);
        }
    }

    [SkippableFact]
    public async Task Linux_secret_service_round_trips_a_refresh_token()
    {
        Skip.IfNot(
            OperatingSystem.IsLinux(),
            "The secret-tool store only runs on Linux.");
        Skip.IfNot(
            ExecutableOnPath(LinuxTokenStore.Tool),
            "secret-tool is not installed.");
        Skip.IfNot(
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")),
            "No D-Bus session bus is available.");

        var service = $"infraorgit-tests-{Guid.NewGuid():N}";
        var account = $"https://secret-tool-test-{Guid.NewGuid():N}.invalid";
        var token = "refresh-" + Guid.NewGuid().ToString("N");
        var store = new LinuxTokenStore(new ProcessRunner(), service);
        using var timeout = new CancellationTokenSource(Timeout);

        try
        {
            try
            {
                await store.SaveAsync(account, token, timeout.Token);
            }
            catch (TokenStoreException exception)
            {
                Skip.If(
                    true,
                    "The Secret Service is not available in this session: " +
                    exception.Message);
            }
            catch (OperationCanceledException)
            {
                Skip.If(true, "secret-tool did not answer in time.");
            }

            var loaded = await store.LoadAsync(account, timeout.Token);
            Assert.True(
                string.Equals(token, loaded, StringComparison.Ordinal),
                "secret-tool did not return the saved refresh token.");

            await store.DeleteAsync(account, timeout.Token);
            Assert.Null(await store.LoadAsync(account, timeout.Token));
        }
        finally
        {
            await TryDeleteAsync(store, account);
        }
    }

    private static async Task TryDeleteAsync(ITokenStore store, string account)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await store.DeleteAsync(account, timeout.Token);
        }
        catch (Exception exception) when (
            exception is TokenStoreException or OperationCanceledException)
        {
            // Best effort: the unique service/account suffix keeps a leftover
            // item away from real instance entries.
        }
    }

    private static bool ExecutableOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, name)));
    }
}
