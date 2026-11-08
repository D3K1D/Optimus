using System.Runtime.InteropServices;

namespace OptimizationTool.Windows.Security;

/// <summary>Security domain P/Invoke and COM declarations. Read-only calls only.</summary>
internal static class Native
{
    // ---- Windows Security Center (wscapi.dll) ----
    public const int WSC_SECURITY_PROVIDER_FIREWALL = 0x1;
    public const int WSC_SECURITY_PROVIDER_ANTIVIRUS = 0x4;

    [DllImport("wscapi.dll")]
    public static extern int WscGetSecurityProviderHealth(int providers, out int health);

    public static readonly Guid CLSID_WSCProductList = new("17072F7B-9ABE-4A74-A261-1EB76B55107A");

    // ---- IP Helper ----
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    public const int UDP_TABLE_OWNER_PID = 1;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll")]
    public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    public static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    // ---- Processes ----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // Process image and start time for PIDs a standard user cannot open (no handle needed).
    public const int SystemProcessInformation = 5;
    public const int SystemProcessIdInformation = 88;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryDosDeviceW(string deviceName, char[] target, int max);

    // ---- Firmware ----
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFirmwareType(out int firmwareType);

    // ---- WinVerifyTrust (wintrust.dll) ----
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    public static readonly Guid DRIVER_ACTION_VERIFY = new("F750E6C3-38EE-11d1-85E5-00C04FC295EE");
    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_CHOICE_CATALOG = 2;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    public const uint WTD_DISABLE_MD2_MD4 = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
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
    public static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll")]
    public static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll")]
    public static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIdx, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIdx);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CertGetNameStringW(IntPtr certContext, uint type, uint flags, IntPtr typePara, char[]? name, uint size);
    public const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;

    // CRYPT_PROVIDER_SGNR (x64): cbStruct@0, sftVerifyAsOf FILETIME@4 (4-byte aligned), csCertChain@12, pasCertChain*@16.
    public const int SGNR_csCertChain = 12;
    public const int SGNR_pasCertChain = 16;
    // CRYPT_PROVIDER_CERT (x64): cbStruct(4)+pad(4), pCert*(8)
    public const int PROVCERT_pCert = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, ref Guid subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    public static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, IntPtr prevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO info, uint flags);

    // ---- Shell links (read-only: Load with STGM_READ, GetPath; never Resolve) ----
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    public const int STGM_READ = 0;
    public const uint SLGP_RAWPATH = 0x4;
}

// ---- COM interfaces ----

[ComImport, Guid("722A338C-6E8E-4E72-AC27-1417FB0C81C2"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWSCProductList
{
    void Initialize(uint provider);
    int GetCount();
    IWscProduct GetItem(uint index);
}

[ComImport, Guid("8C38232E-3A45-4A27-92B0-1A16A975F669"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWscProduct
{
    [return: MarshalAs(UnmanagedType.BStr)] string GetProductName();
    int GetProductState();
    int GetSignatureStatus();
    [return: MarshalAs(UnmanagedType.BStr)] string GetRemediationPath();
    [return: MarshalAs(UnmanagedType.BStr)] string GetProductStateTimestamp();
    [return: MarshalAs(UnmanagedType.BStr)] string GetProductGuid();
    int GetProductIsDefault();
}

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int cch, IntPtr findData, uint flags);
    // Slots 2–7 keep vtable order; the setters are never called (read-only use).
    [PreserveSig] int GetIDList(out IntPtr pidl);
    [PreserveSig] int SetIDList(IntPtr pidl);
    [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] name, int cch);
    [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] dir, int cch);
    [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] args, int cch);
    // Later methods are not declared and never called.
}

[ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    [PreserveSig] int GetClassID(out Guid classId);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
}
