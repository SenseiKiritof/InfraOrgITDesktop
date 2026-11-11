using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace InfraOrgIt.Desktop.Auth;

/// <summary>
/// Windows Credential Manager through advapi32. Each refresh token is a
/// generic credential named <c>InfraOrgIT:&lt;instance URL&gt;</c>; the blob is
/// UTF-8. The blob limit is 2560 bytes (CRED_MAX_CREDENTIAL_BLOB_SIZE) and a
/// longer refresh token fails, which degrades to the memory store.
/// </summary>
public sealed class WindowsTokenStore : ITokenStore
{
    internal const string TargetPrefix = "InfraOrgIT:";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly string _targetPrefix;

    public WindowsTokenStore()
        : this(TargetPrefix)
    {
    }

    /// <summary>
    /// Test seam: a unique prefix keeps a test credential away from the real
    /// instance entries.
    /// </summary>
    internal WindowsTokenStore(string targetPrefix) =>
        _targetPrefix = targetPrefix;

    public bool IsPersistent => true;

    public string Backend => "Credential Manager";

    public Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(Target(instanceUrl)));
    }

    public Task SaveAsync(
        string instanceUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Token vuoto.", nameof(refreshToken));
        cancellationToken.ThrowIfCancellationRequested();
        Write(Target(instanceUrl), refreshToken);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delete(Target(instanceUrl));
        return Task.CompletedTask;
    }

    private string Target(string instanceUrl) =>
        _targetPrefix + TokenStoreKey.Normalize(instanceUrl);

    private static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;
            throw new TokenStoreException(
                $"Lettura da Credential Manager non riuscita (codice {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 ||
                credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var token = Encoding.UTF8.GetString(bytes).Trim('\0');
            return token.Length == 0 ? null : token;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void Write(string target, string refreshToken)
    {
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "infraorgit"
            };

            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new TokenStoreException(
                    $"Salvataggio in Credential Manager non riuscito (codice {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private static void Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
            return;

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new TokenStoreException(
                $"Rimozione da Credential Manager non riuscita (codice {error}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
