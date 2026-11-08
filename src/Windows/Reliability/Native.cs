using System.Runtime.InteropServices;

namespace OptimizationTool.Windows.Reliability;

// P/Invoke for the reliability domain (wevtapi, setupapi/cfgmgr32, user32). Read-only calls only.
internal static class Native
{
    // ---- Event log (wevtapi) ----
    public const int EvtQueryChannelPath = 0x1;
    public const int EvtQueryReverseDirection = 0x200;
    public const int EvtRenderEventXml = 1;
    public const int EvtChannelConfigEnabled = 0;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_TIMEOUT = 1460;
    public const int ERROR_EVT_CHANNEL_NOT_FOUND = 15007;
    public const int ERROR_EVT_INVALID_CHANNEL_PATH = 15000;

    [DllImport("wevtapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    public static extern bool EvtNext(IntPtr resultSet, int eventArraySize, [Out] IntPtr[] eventArray, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    public static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, IntPtr buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr EvtOpenChannelConfig(IntPtr session, string channelPath, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    public static extern bool EvtGetChannelConfigProperty(IntPtr channelConfig, int propertyId, int flags, int bufferSize, IntPtr buffer, out int bufferUsed);

    [DllImport("wevtapi.dll")]
    public static extern bool EvtClose(IntPtr handle);

    // ---- Devices (setupapi / cfgmgr32) ----
    public const int DIGCF_PRESENT = 0x2;
    public const int DIGCF_ALLCLASSES = 0x4;
    public const int CR_SUCCESS = 0;
    public const int DN_HAS_PROBLEM = 0x400;
    public const int DEVPROP_TYPE_STRING = 0x12;
    public const int DEVPROP_TYPE_FILETIME = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY(Guid fmtid, int pid)
    {
        public Guid fmtid = fmtid;
        public int pid = pid;
    }

    private static readonly Guid DriverGuid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid DeviceGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    public static readonly DEVPROPKEY DEVPKEY_Device_DriverDate = new(DriverGuid, 2);
    public static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = new(DriverGuid, 3);
    public static readonly DEVPROPKEY DEVPKEY_Device_DriverProvider = new(DriverGuid, 9);
    public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new(DeviceGuid, 2);
    public static readonly DEVPROPKEY DEVPKEY_Device_Class = new(DeviceGuid, 9);
    public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(DeviceGuid, 14);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, in DEVPROPKEY propertyKey,
        out int propertyType, byte[]? propertyBuffer, int propertyBufferSize, out int requiredSize, int flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, char[] deviceInstanceId, int size, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out int status, out int problemNumber, int devInst, int flags);

    // ---- Displays (user32) ----
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    public const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    public const int DM_INTERLACED = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevicesW(string? device, int devNum, ref DISPLAY_DEVICE displayDevice, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsExW(string deviceName, int modeNum, ref DEVMODE devMode, int flags);
}
