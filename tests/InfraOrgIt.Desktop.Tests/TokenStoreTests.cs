using InfraOrgIt.Desktop.Auth;

namespace InfraOrgIt.Desktop.Tests;

public sealed class TokenStoreTests
{
    private const string Url = "https://infra.example.com";
    private const string Token = "refresh-token";

    [Fact]
    public async Task Memory_store_saves_loads_and_deletes()
    {
        var store = new MemoryTokenStore();

        Assert.Null(await store.LoadAsync(Url));
        Assert.False(store.IsPersistent);

        await store.SaveAsync(Url, Token);
        Assert.Equal(Token, await store.LoadAsync(Url));
        Assert.Equal(Token, await store.LoadAsync(Url + "/"));

        await store.DeleteAsync(Url);
        Assert.Null(await store.LoadAsync(Url));
    }

    [Fact]
    public async Task Mac_store_builds_the_security_commands()
    {
        var runner = new RecordingRunner((_, _, _) => new ProcessResult(0, "", ""));
        var store = new MacTokenStore(runner);

        await store.SaveAsync(Url, Token);
        await store.LoadAsync(Url);
        await store.DeleteAsync(Url);

        Assert.Collection(
            runner.Calls,
            save =>
            {
                Assert.Equal("/usr/bin/security", save.FileName);
                Assert.Equal(
                    [
                        "add-generic-password", "-U", "-s", "InfraOrgIT Desktop",
                        "-a", Url, "-w", Token
                    ],
                    save.Arguments);
                Assert.Null(save.StandardInput);
            },
            load =>
            {
                Assert.Equal(
                    [
                        "find-generic-password", "-s", "InfraOrgIT Desktop",
                        "-a", Url, "-w"
                    ],
                    load.Arguments);
            },
            delete =>
            {
                Assert.Equal(
                    [
                        "delete-generic-password", "-s", "InfraOrgIT Desktop",
                        "-a", Url
                    ],
                    delete.Arguments);
            });
    }

    [Fact]
    public async Task Mac_load_returns_null_when_the_item_is_missing()
    {
        var runner = new RecordingRunner(
            (_, _, _) => new ProcessResult(44, "", "not found"));
        var store = new MacTokenStore(runner);

        Assert.Null(await store.LoadAsync(Url));
    }

    [Fact]
    public async Task Mac_failures_do_not_leak_the_token()
    {
        var runner = new RecordingRunner(
            (_, _, _) => new ProcessResult(1, Token, Token));
        var store = new MacTokenStore(runner);

        var exception = await Assert.ThrowsAsync<TokenStoreException>(
            () => store.LoadAsync(Url));

        Assert.Contains("codice 1", exception.Message);
        Assert.DoesNotContain(Token, exception.Message);
    }

    [Fact]
    public async Task Linux_store_writes_the_token_on_stdin()
    {
        var runner = new RecordingRunner((_, _, _) =>
            new ProcessResult(0, Token, ""));
        var store = new LinuxTokenStore(runner);

        await store.SaveAsync(Url, Token);
        Assert.Equal(Token, await store.LoadAsync(Url));
        await store.DeleteAsync(Url);

        Assert.Collection(
            runner.Calls,
            save =>
            {
                Assert.Equal("secret-tool", save.FileName);
                Assert.Equal(
                    [
                        "store", "--label=InfraOrgIT Desktop", "service",
                        "infraorgit", "account", Url
                    ],
                    save.Arguments);
                Assert.Equal(Token, save.StandardInput);
            },
            load =>
            {
                Assert.Equal(
                    [
                        "lookup", "service", "infraorgit", "account", Url
                    ],
                    load.Arguments);
                Assert.Null(load.StandardInput);
            },
            delete =>
            {
                Assert.Equal(
                    [
                        "clear", "service", "infraorgit", "account", Url
                    ],
                    delete.Arguments);
            });
    }

    [Fact]
    public async Task Linux_load_returns_null_when_the_item_is_missing()
    {
        var runner = new RecordingRunner(
            (_, _, _) => new ProcessResult(1, "", ""));
        var store = new LinuxTokenStore(runner);

        Assert.Null(await store.LoadAsync(Url));
    }

    [Fact]
    public async Task Fallback_degrades_to_memory_and_warns_once()
    {
        var warnings = new List<string>();
        var store = new FallbackTokenStore(new FailingStore(), warnings.Add);

        await store.SaveAsync(Url, Token);
        Assert.Equal(Token, await store.LoadAsync(Url));
        Assert.False(store.IsPersistent);
        Assert.Equal("memoria", store.Backend);
        Assert.Single(warnings);

        await store.DeleteAsync(Url);
        Assert.Null(await store.LoadAsync(Url));
        Assert.Single(warnings);
    }

    private sealed class RecordingRunner(
        Func<string, IReadOnlyList<string>, string?, ProcessResult> respond)
        : IProcessRunner
    {
        public List<Call> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(fileName, arguments.ToArray(), standardInput));
            return Task.FromResult(respond(fileName, arguments, standardInput));
        }

        public sealed record Call(
            string FileName,
            string[] Arguments,
            string? StandardInput);
    }

    private sealed class FailingStore : ITokenStore
    {
        public bool IsPersistent => true;

        public string Backend => "failing";

        public Task<string?> LoadAsync(
            string instanceUrl, CancellationToken cancellationToken = default) =>
            throw new TokenStoreException("non disponibile");

        public Task SaveAsync(
            string instanceUrl,
            string refreshToken,
            CancellationToken cancellationToken = default) =>
            throw new TokenStoreException("non disponibile");

        public Task DeleteAsync(
            string instanceUrl, CancellationToken cancellationToken = default) =>
            throw new TokenStoreException("non disponibile");
    }
}
