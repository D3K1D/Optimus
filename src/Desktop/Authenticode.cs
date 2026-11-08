using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OptimizationTool.Desktop;

/// <summary>
/// Embedded Authenticode check (WinVerifyTrust) that returns the leaf signer certificate's subject O= value when the
/// signature is valid. Offline only: no revocation (<c>WTD_REVOKE_NONE</c>), cache-only URL retrieval, no UI, no catalog
/// lookup. Same approach as src/Windows/Security/Common/Signature.cs, self-contained here. Results are cached per
/// path + size + last write time.
/// </summary>
internal static class Authenticode
{
    private static readonly Dictionary<(string, long, DateTime), string?> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>The leaf subject's organization when <paramref name="path"/> carries a valid embedded signature; otherwise null.</summary>
    public static string? ValidPublisher(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var key = (path.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc);
            lock (CacheLock) if (Cache.TryGetValue(key, out var hit)) return hit;
            var publisher = Verify(path);
            lock (CacheLock) Cache[key] = publisher;
            return publisher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Verify(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, infoPtr, false);
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pInfo = infoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_DISABLE_MD2_MD4,
            };
            var hr = WinVerifyTrust(new IntPtr(-1) /* INVALID_HANDLE_VALUE: no UI */, ref action, ref data);
            try
            {
                return hr == 0 && data.hWVTStateData != IntPtr.Zero ? LeafOrganization(data.hWVTStateData) : null;
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static string? LeafOrganization(IntPtr state)
    {
        var prov = WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero) return null;
        var sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (sgnr == IntPtr.Zero) return null;
        if (Marshal.ReadInt32(sgnr, SGNR_csCertChain) < 1) return null;
        var chain = Marshal.ReadIntPtr(sgnr, SGNR_pasCertChain);
        if (chain == IntPtr.Zero) return null;
        var certContext = Marshal.ReadIntPtr(chain, PROVCERT_pCert);
        if (certContext == IntPtr.Zero) return null;
        using var cert = new X509Certificate2(certContext); // duplicates the context
        return Organization(cert.SubjectName);
    }

    /// <summary>The single O= (2.5.4.10) value of a distinguished name, or null when absent or repeated.</summary>
    internal static string? Organization(X500DistinguishedName name)
    {
        string? found = null;
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements || rdn.GetSingleElementType().Value != "2.5.4.10") continue;
            if (found is not null) return null;
            found = rdn.GetSingleElementValue();
        }
        return found;
    }

    // ---- wintrust.dll ----

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private const uint WTD_DISABLE_MD2_MD4 = 0x2000;

    // CRYPT_PROVIDER_SGNR (x64): cbStruct@0, sftVerifyAsOf FILETIME@4, csCertChain@12, pasCertChain*@16.
    private const int SGNR_csCertChain = 12;
    private const int SGNR_pasCertChain = 16;
    // CRYPT_PROVIDER_CERT (x64): cbStruct(4)+pad(4), pCert*(8).
    private const int PROVCERT_pCert = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIdx, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIdx);
}
