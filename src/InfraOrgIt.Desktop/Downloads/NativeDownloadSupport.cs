using System.Runtime.InteropServices;
using Photino.NET;

namespace InfraOrgIt.Desktop.Downloads;

/// <summary>
/// Opt-in macOS Photino.Native replacement with WKDownload support. The
/// patched universal dylib is built from native/photino-download and shipped
/// next to the app; when it is absent the managed bridge remains the only
/// download route.
/// </summary>
internal static class NativeDownloadSupport
{
    private const string LibraryName = "Photino.Native";

    public static bool TryRegister()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var path = Path.Combine(
            AppContext.BaseDirectory,
            "native",
            "Photino.Native.dylib");
        if (!File.Exists(path))
            return false;

        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(PhotinoWindow).Assembly,
                (name, _, _) =>
                    name is LibraryName or "Photino.Native.dylib"
                        ? NativeLibrary.Load(path)
                        : IntPtr.Zero);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The resolver can be registered only once per assembly.
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}