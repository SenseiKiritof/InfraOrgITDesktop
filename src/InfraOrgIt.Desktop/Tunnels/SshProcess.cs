using System.Diagnostics;

namespace InfraOrgIt.Desktop.Tunnels;

/// <summary>
/// Handle to the ssh child process. The manager only needs its lifecycle and
/// stderr, so tests can replace it without spawning a real ssh.
/// </summary>
internal interface ISshProcess : IDisposable
{
    event Action<string>? ErrorLine;

    void Start();

    void Kill();

    Task WaitForExitAsync();

    int ExitCode { get; }
}

internal sealed class SystemSshProcess(ProcessStartInfo startInfo) : ISshProcess
{
    private readonly Process _process = new() { StartInfo = startInfo };

    public event Action<string>? ErrorLine;

    public void Start()
    {
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                ErrorLine?.Invoke(args.Data);
        };
        _process.Start();
        _process.BeginErrorReadLine();
    }

    public void Kill() => _process.Kill(entireProcessTree: true);

    public Task WaitForExitAsync() => _process.WaitForExitAsync();

    public int ExitCode => _process.ExitCode;

    public void Dispose() => _process.Dispose();
}
