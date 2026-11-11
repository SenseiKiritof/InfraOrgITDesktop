using System.Runtime.InteropServices;
using System.Text;

namespace InfraOrgIt.Desktop.Auth;

/// <summary>
/// macOS login Keychain through Security.framework. The refresh token is the
/// generic-password value; the instance URL is the account name. The native
/// calls keep the token out of the process argument list (PT-16), unlike the
/// former <c>/usr/bin/security</c> path. A missing, locked or refused Keychain
/// surfaces as <see cref="TokenStoreException"/>, so the caller falls back to
/// memory.
/// </summary>
public sealed class MacTokenStore : ITokenStore
{
    internal const string Service = "InfraOrgIT Desktop";
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // OSStatus values from SecBase.h.
    private const int Success = 0;
    private const int DuplicateItem = -25299; // errSecDuplicateItem
    private const int ItemNotFound = -25300; // errSecItemNotFound

    private readonly string _service;

    public MacTokenStore()
        : this(Service)
    {
    }

    /// <summary>
    /// Test seam: a unique service keeps a test item away from the real
    /// instance entries.
    /// </summary>
    internal MacTokenStore(string service) => _service = service;

    public bool IsPersistent => true;

    public string Backend => "Portachiavi";

    public Task<string?> LoadAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = Find(
            _service, TokenStoreKey.Normalize(instanceUrl), out var item,
            out var password);
        try
        {
            if (status == ItemNotFound)
                return Task.FromResult<string?>(null);
            if (status != Success)
                throw Failure("Lettura dal Portachiavi", status);

            return Task.FromResult(
                string.IsNullOrEmpty(password) ? null : password);
        }
        finally
        {
            Release(item);
        }
    }

    public Task SaveAsync(
        string instanceUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Token vuoto.", nameof(refreshToken));
        cancellationToken.ThrowIfCancellationRequested();

        var account = TokenStoreKey.Normalize(instanceUrl);
        var status = Add(_service, account, refreshToken);

        // An item written by an earlier build or by a previous sign-in for the
        // same instance already exists: replace its password in place.
        if (status == DuplicateItem)
            status = Replace(_service, account, refreshToken);
        if (status != Success)
            throw Failure("Salvataggio nel Portachiavi", status);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string instanceUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = Find(
            _service, TokenStoreKey.Normalize(instanceUrl), out var item,
            out _);
        try
        {
            if (status == ItemNotFound)
                return Task.CompletedTask;
            if (status != Success)
                throw Failure("Rimozione dal Portachiavi", status);

            status = SecKeychainItemDelete(item);
            if (status is not (Success or ItemNotFound))
                throw Failure("Rimozione dal Portachiavi", status);
        }
        finally
        {
            Release(item);
        }

        return Task.CompletedTask;
    }

    private static int Add(string service, string account, string token)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var tokenBytes = Encoding.UTF8.GetBytes(token);

        return SecKeychainAddGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            (uint)tokenBytes.Length, tokenBytes,
            IntPtr.Zero);
    }

    private static int Replace(string service, string account, string token)
    {
        var status = Find(service, account, out var item, out _);
        try
        {
            if (status == ItemNotFound)
                return Add(service, account, token);
            if (status != Success)
                return status;

            var tokenBytes = Encoding.UTF8.GetBytes(token);
            return SecKeychainItemModifyContent(
                item, IntPtr.Zero, (uint)tokenBytes.Length, tokenBytes);
        }
        finally
        {
            Release(item);
        }
    }

    private static int Find(
        string service,
        string account,
        out IntPtr item,
        out string? password)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);

        // Initialize the out values: a failing native call must not leave a
        // stale pointer behind for the release paths.
        item = IntPtr.Zero;
        var data = IntPtr.Zero;
        uint length = 0;
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            out length, out data, out item);

        password = null;
        if (status != Success)
        {
            Release(item);
            item = IntPtr.Zero;
            return status;
        }

        try
        {
            if (data != IntPtr.Zero && length > 0)
            {
                var bytes = new byte[(int)length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                password = Encoding.UTF8.GetString(bytes);
            }
        }
        finally
        {
            if (data != IntPtr.Zero)
                SecKeychainItemFreeContent(IntPtr.Zero, data);
        }

        return status;
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
            CFRelease(item);
    }

    private static TokenStoreException Failure(string action, int status) =>
        new($"{action} non riuscita (codice {status}).");

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainAddGenericPassword")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        IntPtr itemRef);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemModifyContent")]
    private static extern int SecKeychainItemModifyContent(
        IntPtr itemRef,
        IntPtr attributeList,
        uint length,
        byte[] data);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemFreeContent")]
    private static extern int SecKeychainItemFreeContent(
        IntPtr attributeList, IntPtr data);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemDelete")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static extern void CFRelease(IntPtr cf);
}
