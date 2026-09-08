using System.Runtime.InteropServices;
using System.Text;

namespace Transworld.PrinterRepair.Infrastructure.Interop;

/// <summary>
/// Firmas P/Invoke del spooler de Windows (winspool.drv) y del gestor de servicios (advapi32).
/// Se usan APIs nativas en lugar de PowerShell para que el ejecutable no dependa de nada
/// instalado en el equipo destino y para poder distinguir errores concretos.
/// </summary>
internal static class NativeMethods
{
    // ---- EnumPrinters ----
    internal const uint PRINTER_ENUM_LOCAL = 0x00000002;
    internal const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    // ---- Accesos ----
    internal const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;
    internal const uint PRINTER_ACCESS_ADMINISTER = 0x00000004;
    internal const uint PRINTER_ACCESS_USE = 0x00000008;
    internal const uint PRINTER_ALL_ACCESS =
        STANDARD_RIGHTS_REQUIRED | PRINTER_ACCESS_ADMINISTER | PRINTER_ACCESS_USE;
    internal const uint SERVER_ACCESS_ADMINISTER = 0x00000001;

    // ---- SetPrinter ----
    internal const uint PRINTER_CONTROL_PURGE = 3;

    // ---- Atributos de impresora ----
    internal const uint PRINTER_ATTRIBUTE_LOCAL = 0x00000040;
    internal const uint PRINTER_ATTRIBUTE_NETWORK = 0x00000010;
    internal const uint PRINTER_ATTRIBUTE_SHARED = 0x00000008;

    // ---- Codigos de error Win32 relevantes ----
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_INVALID_PRINTER_NAME = 1801;
    internal const int ERROR_UNKNOWN_PRINTER_DRIVER = 1797;
    internal const int ERROR_PRINTER_ALREADY_EXISTS = 1802;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_CANCELLED = 1223;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_UNKNOWN_PORT = 1796;

    /// <summary>El spooler aun no ha soltado el puerto tras eliminar la impresora que lo usaba.</summary>
    internal const int ERROR_BUSY = 170;

    // ---- Puerto TCP/IP estandar ----
    internal const uint PROTOCOL_RAWTCP_TYPE = 1;
    internal const uint PROTOCOL_LPR_TYPE = 2;
    internal const string XcvMonitorTcpIp = ",XcvMonitor Standard TCP/IP Port";
    internal const string StandardTcpIpMonitor = "Standard TCP/IP Port";

    // ---- Instalacion de driver desde paquete ----
    internal const uint UPDP_UPLOAD_ALWAYS = 0x00000001;
    internal const uint UPDP_SILENT_UPLOAD = 0x00000002;
    internal const uint UPDP_CHECK_DRIVERSTORE = 0x00000004;
    internal const uint IPDFP_COPY_ALL_FILES = 0x00000001;

    internal const string EnvironmentX64 = "Windows x64";

    // =====================================================================
    // Estructuras
    // =====================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_INFO_2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PORT_INFO_2
    {
        public string? pPortName;
        public string? pMonitorName;
        public string? pDescription;
        public uint fPortType;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DRIVER_INFO_2
    {
        public uint cVersion;
        public string? pName;
        public string? pEnvironment;
        public string? pDriverPath;
        public string? pDataFile;
        public string? pConfigFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public uint DesiredAccess;
    }

    /// <summary>
    /// Estructura del monitor "Standard TCP/IP Port" para XcvData AddPort/ConfigPort.
    /// Los tamanos de los arrays son los del WDK y no se pueden cambiar:
    /// MAX_PORTNAME_LEN 64, MAX_NETWORKNAME_LEN 49, MAX_SNMP_COMMUNITY_STR_LEN 33,
    /// MAX_QUEUENAME_LEN 33, MAX_IPADDR_STR_LEN 16.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PORT_DATA_1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string sztPortName;
        public uint dwVersion;
        public uint dwProtocol;
        public uint cbSize;
        public uint dwReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 49)] public string sztHostAddress;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string sztSNMPCommunity;
        public uint dwDoubleSpool;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string sztQueue;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string sztIPAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 540)] public byte[] Reserved;
        public uint dwPortNumber;
        public uint dwSNMPEnabled;
        public uint dwSNMPDevIndex;
    }

    /// <summary>
    /// Estructura que el monitor TCP/IP espera en XcvData "DeletePort". Pasar el nombre del
    /// puerto como cadena suelta se rechaza con ERROR_INVALID_DATA (13).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DELETE_PORT_DATA_1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string psztPortName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 98)] public byte[] Reserved;
        public uint dwVersion;
        public uint dwReserved;
    }

    // =====================================================================
    // winspool.drv - impresoras
    // =====================================================================

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPrinters(
        uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf,
        out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeletePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "SetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint command);

    [DllImport("winspool.drv", EntryPoint = "AddPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr AddPrinter(string? pName, uint level, ref PRINTER_INFO_2 pPrinter);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPrinter(
        IntPtr hPrinter, uint level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

    [DllImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDefaultPrinter(string pszPrinter);

    [DllImport("winspool.drv", EntryPoint = "DeletePrinterConnectionW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeletePrinterConnection(string pName);

    // =====================================================================
    // winspool.drv - puertos
    // =====================================================================

    [DllImport("winspool.drv", EntryPoint = "EnumPortsW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPorts(
        string? pName, uint level, IntPtr pPorts, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    // Nota: DeletePortW existe, pero abre un dialogo de Windows. No se usa: la reparacion
    // debe completarse sin que el usuario vea ninguna ventana del sistema.

    [DllImport("winspool.drv", EntryPoint = "XcvDataW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool XcvData(
        IntPtr hXcv, string pszDataName, IntPtr pInputData, uint cbInputData,
        IntPtr pOutputData, uint cbOutputData, out uint pcbOutputNeeded, out uint pdwStatus);

    // =====================================================================
    // winspool.drv - drivers
    // =====================================================================

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDriversW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPrinterDrivers(
        string? pName, string? pEnvironment, uint level, IntPtr pDriverInfo, uint cbBuf,
        out uint pcbNeeded, out uint pcReturned);

    /// <summary>Sube el paquete al Driver Store y devuelve la ruta del INF ya almacenado.</summary>
    [DllImport("winspool.drv", EntryPoint = "UploadPrinterDriverPackageW", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int UploadPrinterDriverPackage(
        string? pszServer, string pszInfPath, string? pszEnvironment, uint dwFlags, IntPtr hwnd,
        StringBuilder? pszDestInfPath, ref uint pcchDestInfPath);

    /// <summary>Publica en el spooler un driver que ya esta en el Driver Store.</summary>
    [DllImport("winspool.drv", EntryPoint = "InstallPrinterDriverFromPackageW", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int InstallPrinterDriverFromPackage(
        string? pszServer, string pszInfPath, string? pszDriverName, string? pszEnvironment, uint dwFlags);

    // =====================================================================
    // advapi32 - control del servicio de spooler
    // =====================================================================

    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;
    internal const uint SERVICE_START = 0x0010;
    internal const uint SERVICE_STOP = 0x0020;
    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
    internal const uint SERVICE_CONTROL_STOP = 0x00000001;

    internal const uint SERVICE_STOPPED = 0x00000001;
    internal const uint SERVICE_START_PENDING = 0x00000002;
    internal const uint SERVICE_STOP_PENDING = 0x00000003;
    internal const uint SERVICE_RUNNING = 0x00000004;

    internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    internal const uint SERVICE_AUTO_START = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenService(IntPtr hSCManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartService(IntPtr hService, uint numArgs, IntPtr args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(IntPtr hService, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig(
        IntPtr hService, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);
}
