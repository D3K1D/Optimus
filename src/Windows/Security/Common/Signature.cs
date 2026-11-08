using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Windows.Security.Common;

/// <summary>status: valid | unsigned | invalid | not_found | unknown. Signer is the leaf certificate's display name.</summary>
internal sealed record SignatureInfo(string Status, string? Signer, bool Catalog)
{
    public static readonly SignatureInfo NotFound = new("not_found", null, false);
    public static readonly SignatureInfo Unknown = new("unknown", null, false);

    /// <summary>
    /// Microsoft's own code. "Microsoft Windows Hardware Compatibility Publisher" (WHQL) signs third-party drivers
    /// and their services, so it does not count.
    /// </summary>
    public bool IsMicrosoft => Status == "valid" && Signer is { } s
        && (s.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
        && !s.Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Authenticode check with WinVerifyTrust, embedded signature first, then the system catalog. Offline only:
/// no revocation (<c>WTD_REVOKE_NONE</c>) and cache-only URL retrieval. The catalog lookup computes the file's
/// catalog hash in memory to find its catalog; nothing is stored. No UI. Results are cached per path.
/// </summary>
internal sealed class SignatureChecker : IDisposable
{
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private readonly Dictionary<string, SignatureInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _catAdmin256;
    private IntPtr _catAdmin1;
    private bool _catInit;

    public SignatureInfo Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return SignatureInfo.Unknown;
        if (_cache.TryGetValue(path, out var hit)) return hit;
        var info = CheckUncached(path);
        _cache[path] = info;
        return info;
    }

    private SignatureInfo CheckUncached(string path)
    {
        try
        {
            if (!File.Exists(path)) return SignatureInfo.NotFound;
            var embedded = VerifyFile(path, out var signer);
            if (embedded == 0) return new("valid", signer, false);
            if (embedded is not (TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN))
                return new("invalid", signer, false);
            var cat = VerifyCatalog(path, out signer);
            return cat switch
            {
                null => new("unsigned", null, false),
                0 => new("valid", signer, true),
                _ => new("invalid", signer, true),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SignatureInfo.Unknown;
        }
    }

    private static int VerifyFile(string path, out string? signer)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new Native.WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<Native.WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, infoPtr, false);
            return Verify(Native.WTD_CHOICE_FILE, infoPtr, out signer);
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>null when no catalog lists the file; otherwise the WinVerifyTrust result.</summary>
    private int? VerifyCatalog(string path, out string? signer)
    {
        signer = null;
        EnsureCatAdmin();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var admin in new[] { _catAdmin256, _catAdmin1 })
        {
            if (admin == IntPtr.Zero) continue;
            var hash = Hash(admin, fs.SafeFileHandle);
            if (hash is null) continue;
            var catInfo = Native.CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
            if (catInfo == IntPtr.Zero) continue;
            try
            {
                var ci = new Native.CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<Native.CATALOG_INFO>(), wszCatalogFile = "" };
                if (!Native.CryptCATCatalogInfoFromContext(catInfo, ref ci, 0)) continue;
                return VerifyCatalogMember(path, ci.wszCatalogFile, hash, admin, out signer);
            }
            finally
            {
                Native.CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
            }
        }
        return null;
    }

    private static byte[]? Hash(IntPtr admin, SafeFileHandle file)
    {
        uint size = 0;
        Native.CryptCATAdminCalcHashFromFileHandle2(admin, file.DangerousGetHandle(), ref size, null, 0);
        if (size == 0 || size > 128) return null;
        var hash = new byte[size];
        return Native.CryptCATAdminCalcHashFromFileHandle2(admin, file.DangerousGetHandle(), ref size, hash, 0) ? hash : null;
    }

    private static int VerifyCatalogMember(string path, string catalogFile, byte[] hash, IntPtr admin, out string? signer)
    {
        var catPtr = Marshal.StringToHGlobalUni(catalogFile);
        var tagPtr = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
        var memberPtr = Marshal.StringToHGlobalUni(path);
        var hashPtr = Marshal.AllocHGlobal(hash.Length);
        var size = Marshal.SizeOf<Native.WINTRUST_CATALOG_INFO>();
        var infoPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(hash, 0, hashPtr, hash.Length);
            var info = new Native.WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)size,
                pcwszCatalogFilePath = catPtr,
                pcwszMemberTag = tagPtr,
                pcwszMemberFilePath = memberPtr,
                pbCalculatedFileHash = hashPtr,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = admin,
            };
            Marshal.StructureToPtr(info, infoPtr, false);
            return Verify(Native.WTD_CHOICE_CATALOG, infoPtr, out signer);
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(hashPtr);
            Marshal.FreeHGlobal(memberPtr);
            Marshal.FreeHGlobal(tagPtr);
            Marshal.FreeHGlobal(catPtr);
        }
    }

    private static int Verify(uint choice, IntPtr info, out string? signer)
    {
        signer = null;
        var action = Native.WINTRUST_ACTION_GENERIC_VERIFY_V2;
        var data = new Native.WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<Native.WINTRUST_DATA>(),
            dwUIChoice = Native.WTD_UI_NONE,
            fdwRevocationChecks = Native.WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pInfo = info,
            dwStateAction = Native.WTD_STATEACTION_VERIFY,
            dwProvFlags = Native.WTD_REVOCATION_CHECK_NONE | Native.WTD_CACHE_ONLY_URL_RETRIEVAL | Native.WTD_DISABLE_MD2_MD4,
        };
        var hr = Native.WinVerifyTrust(new IntPtr(-1) /* INVALID_HANDLE_VALUE: no UI */, ref action, ref data);
        try
        {
            if (data.hWVTStateData != IntPtr.Zero) signer = SignerName(data.hWVTStateData);
        }
        finally
        {
            data.dwStateAction = Native.WTD_STATEACTION_CLOSE;
            Native.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        return hr;
    }

    private static string? SignerName(IntPtr state)
    {
        var prov = Native.WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero) return null;
        var sgnr = Native.WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (sgnr == IntPtr.Zero) return null;
        if (Marshal.ReadInt32(sgnr, Native.SGNR_csCertChain) < 1) return null;
        var chain = Marshal.ReadIntPtr(sgnr, Native.SGNR_pasCertChain);
        if (chain == IntPtr.Zero) return null;
        var cert = Marshal.ReadIntPtr(chain, Native.PROVCERT_pCert);
        if (cert == IntPtr.Zero) return null;
        var len = Native.CertGetNameStringW(cert, Native.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, null, 0);
        if (len <= 1 || len > 1024) return null;
        var buf = new char[len];
        Native.CertGetNameStringW(cert, Native.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, IntPtr.Zero, buf, len);
        return Text.Clean(new string(buf, 0, (int)len - 1), 120);
    }

    private void EnsureCatAdmin()
    {
        if (_catInit) return;
        _catInit = true;
        var g = Native.DRIVER_ACTION_VERIFY;
        if (!Native.CryptCATAdminAcquireContext2(out _catAdmin256, ref g, "SHA256", IntPtr.Zero, 0)) _catAdmin256 = IntPtr.Zero;
        g = Native.DRIVER_ACTION_VERIFY;
        if (!Native.CryptCATAdminAcquireContext2(out _catAdmin1, ref g, "SHA1", IntPtr.Zero, 0)) _catAdmin1 = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_catAdmin256 != IntPtr.Zero) Native.CryptCATAdminReleaseContext(_catAdmin256, 0);
        if (_catAdmin1 != IntPtr.Zero) Native.CryptCATAdminReleaseContext(_catAdmin1, 0);
        _catAdmin256 = _catAdmin1 = IntPtr.Zero;
    }
}
