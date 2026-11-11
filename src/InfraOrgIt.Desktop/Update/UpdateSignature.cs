using System.Security.Cryptography;
using System.Text;

namespace InfraOrgIt.Desktop.Update;

/// <summary>
/// Verifies the detached RSA/SHA-256 signature of the release checksum
/// manifest. The updater trusts an archive hash only when it comes from a
/// manifest whose signature matches the embedded public key; a SHA-256 file
/// published next to an unsigned release is never enough.
/// </summary>
internal static class UpdateSignature
{
    internal const string PublicKeyResourceName =
        "InfraOrgIt.Desktop.Update.update-public.pem";

    /// <summary>
    /// Public key shipped with the app. <c>null</c> when the embedded resource
    /// is missing, which keeps the updater on the unsigned path instead of
    /// throwing.
    /// </summary>
    internal static string? ProductionPublicKeyPem { get; } = LoadPublicKey();

    internal static bool Verify(
        byte[] manifest, byte[] signature, string? publicKeyPem)
    {
        if (manifest.Length == 0 ||
            signature.Length == 0 ||
            string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(
                manifest,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? LoadPublicKey()
    {
        using var stream = typeof(UpdateSignature).Assembly
            .GetManifestResourceStream(PublicKeyResourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The checksum manifest published by <c>publish.yml</c>: one
/// <c>&lt;sha256&gt;  &lt;file name&gt;</c> line per archive, sorted by name.
/// Only parsed after <see cref="UpdateSignature"/> accepted the signature.
/// </summary>
internal sealed class UpdateManifest
{
    private readonly Dictionary<string, string> _hashes;

    private UpdateManifest(Dictionary<string, string> hashes) => _hashes = hashes;

    internal static UpdateManifest? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var tokens = line.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return null;

            var hash = tokens[0];
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
                return null;

            var name = tokens[^1];
            if (name.StartsWith('*'))
                name = name[1..];
            if (name.Length == 0)
                return null;

            if (!hashes.TryAdd(name, hash) &&
                !string.Equals(hashes[name], hash, StringComparison.OrdinalIgnoreCase))
            {
                // A file listed twice with different hashes is ambiguous.
                return null;
            }
        }

        return hashes.Count == 0 ? null : new UpdateManifest(hashes);
    }

    /// <summary>Hash for an exact asset name, or null when it is not listed.</summary>
    internal string? FindHash(string assetName) =>
        _hashes.TryGetValue(assetName, out var hash) ? hash : null;
}
