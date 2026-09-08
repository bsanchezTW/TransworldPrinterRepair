using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Transworld.PrinterRepair.Infrastructure.Interop;
using Transworld.PrinterRepair.Services;
using static Transworld.PrinterRepair.Infrastructure.Interop.NativeMethods;

namespace Transworld.PrinterRepair.Infrastructure;

/// <summary>Una cola de impresion tal y como la ve el spooler.</summary>
public sealed record PrinterInfo(
    string Name,
    string DriverName,
    string PortName,
    uint Attributes,
    uint Status)
{
    public bool IsNetwork => (Attributes & PRINTER_ATTRIBUTE_NETWORK) != 0;
}

/// <summary>Acceso al spooler de Windows. Es la unica clase que habla con winspool.drv.</summary>
public sealed class PrinterApi
{
    private readonly LoggingService _log;

    public PrinterApi(LoggingService log) => _log = log;

    // =====================================================================
    // Impresoras
    // =====================================================================

    public IReadOnlyList<PrinterInfo> ListPrinters()
    {
        const uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;

        EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return Array.Empty<PrinterInfo>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, 2, buffer, needed, out _, out var count))
                throw LastError("EnumPrinters");

            var result = new List<PrinterInfo>((int)count);
            var size = Marshal.SizeOf<PRINTER_INFO_2>();

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer + i * size);
                result.Add(new PrinterInfo(
                    item.pPrinterName ?? string.Empty,
                    item.pDriverName ?? string.Empty,
                    item.pPortName ?? string.Empty,
                    item.Attributes,
                    item.Status));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public PrinterInfo? GetPrinter(string name)
    {
        if (!TryOpen(name, PRINTER_ALL_ACCESS, out var handle))
        {
            // Sin permisos de administracion se reintenta con acceso de solo lectura.
            if (!NativeMethods.OpenPrinter(name, out handle, IntPtr.Zero)) return null;
        }

        try
        {
            NativeMethods.GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!NativeMethods.GetPrinter(handle, 2, buffer, needed, out _)) return null;

                var item = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
                return new PrinterInfo(
                    item.pPrinterName ?? string.Empty,
                    item.pDriverName ?? string.Empty,
                    item.pPortName ?? string.Empty,
                    item.Attributes,
                    item.Status);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>
    /// Vacia la cola y elimina la impresora. Purgar primero evita que DeletePrinter falle
    /// con trabajos atascados, que es justo el estado en el que suele estar un equipo averiado.
    /// </summary>
    public void DeletePrinter(string name)
    {
        if (!TryOpen(name, PRINTER_ALL_ACCESS, out var handle))
            throw LastError("OpenPrinter(" + name + ")");

        try
        {
            if (!SetPrinter(handle, 0, IntPtr.Zero, PRINTER_CONTROL_PURGE))
                _log.Warn($"No se pudo purgar la cola de {name}: {LastError("SetPrinter").Message}");

            if (!NativeMethods.DeletePrinter(handle))
                throw LastError("DeletePrinter(" + name + ")");
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>Elimina una conexion a impresora compartida del perfil del usuario actual.</summary>
    public bool TryDeletePrinterConnection(string name) => DeletePrinterConnection(name);

    public void AddPrinter(string printerName, string portName, string driverName)
    {
        if (!Validation.IsValidPrinterName(printerName))
            throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                "Nombre de impresora no valido: " + printerName);

        var info = new PRINTER_INFO_2
        {
            pPrinterName = printerName,
            pPortName = portName,
            pDriverName = driverName,
            pPrintProcessor = "winprint",
            pDatatype = "RAW",
            pComment = "Configurada por AutoReparacion de Impresoras Transworld",
            pLocation = string.Empty,
            pShareName = string.Empty,
            pSepFile = string.Empty,
            pParameters = string.Empty,
            Attributes = PRINTER_ATTRIBUTE_LOCAL,
        };

        var handle = NativeMethods.AddPrinter(null, 2, ref info);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw error switch
            {
                ERROR_UNKNOWN_PRINTER_DRIVER => new RepairException(
                    RepairErrorCode.DriverNoInstalado,
                    $"El spooler no reconoce el driver '{driverName}' (Win32 {error})."),
                ERROR_ACCESS_DENIED => new RepairException(
                    RepairErrorCode.PermisosInsuficientes,
                    $"Acceso denegado al crear '{printerName}' (Win32 {error})."),
                _ => new RepairException(
                    RepairErrorCode.ImpresoraNoCreada,
                    $"AddPrinter fallo para '{printerName}' (Win32 {error}).",
                    new Win32Exception(error)),
            };
        }

        ClosePrinter(handle);
    }

    /// <summary>
    /// Marca la impresora como predeterminada. IMPORTANTE: debe llamarse desde el proceso
    /// NO elevado. La predeterminada es una preferencia por usuario, y si el UAC se acepto
    /// con las credenciales de otro administrador, el worker elevado la aplicaria a esa
    /// otra cuenta y el trabajador no la veria.
    /// </summary>
    public bool TrySetDefaultPrinter(string printerName)
    {
        if (SetDefaultPrinter(printerName)) return true;
        _log.Warn($"No se pudo marcar {printerName} como predeterminada: {LastError("SetDefaultPrinter").Message}");
        return false;
    }

    // =====================================================================
    // Drivers
    // =====================================================================

    public IReadOnlyList<string> ListInstalledDriverNames()
    {
        EnumPrinterDrivers(null, EnvironmentX64, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinterDrivers(null, EnvironmentX64, 2, buffer, needed, out _, out var count))
                throw LastError("EnumPrinterDrivers");

            var result = new List<string>((int)count);
            var size = Marshal.SizeOf<DRIVER_INFO_2>();

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<DRIVER_INFO_2>(buffer + i * size);
                if (!string.IsNullOrEmpty(item.pName)) result.Add(item.pName!);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool IsDriverInstalled(string driverName) =>
        ListInstalledDriverNames().Any(n => string.Equals(n, driverName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sube el paquete al Driver Store de Windows y devuelve la ruta del INF ya almacenado.
    /// Es el paso previo documentado a InstallPrinterDriverFromPackage.
    /// </summary>
    public string UploadDriverPackage(string infPath)
    {
        uint capacity = 1024;
        var destination = new StringBuilder((int)capacity);

        var hr = UploadPrinterDriverPackage(
            null, infPath, EnvironmentX64,
            UPDP_UPLOAD_ALWAYS | UPDP_SILENT_UPLOAD,
            IntPtr.Zero, destination, ref capacity);

        // El buffer inicial puede quedarse corto; la API devuelve el tamano necesario.
        if (hr == HResultFromWin32(ERROR_INSUFFICIENT_BUFFER))
        {
            destination = new StringBuilder((int)capacity + 1);
            hr = UploadPrinterDriverPackage(
                null, infPath, EnvironmentX64,
                UPDP_UPLOAD_ALWAYS | UPDP_SILENT_UPLOAD,
                IntPtr.Zero, destination, ref capacity);
        }

        if (hr < 0)
            throw new RepairException(RepairErrorCode.DriverNoInstalado,
                $"UploadPrinterDriverPackage fallo para '{infPath}' (HRESULT 0x{hr:X8}).");

        var stored = destination.ToString();
        _log.Info($"Paquete almacenado en el Driver Store: {stored}");
        return string.IsNullOrWhiteSpace(stored) ? infPath : stored;
    }

    /// <summary>Publica en el spooler un driver que ya esta en el Driver Store.</summary>
    public void PublishDriver(string storeInfPath, string driverName)
    {
        var hr = InstallPrinterDriverFromPackage(
            null, storeInfPath, driverName, EnvironmentX64, IPDFP_COPY_ALL_FILES);

        if (hr < 0)
            throw new RepairException(RepairErrorCode.DriverNoInstalado,
                $"InstallPrinterDriverFromPackage fallo para '{driverName}' " +
                $"desde '{storeInfPath}' (HRESULT 0x{hr:X8}).");

        _log.Info($"Driver publicado en el spooler: {driverName}");
    }

    // =====================================================================
    // Utilidades
    // =====================================================================

    private static bool TryOpen(string name, uint access, out IntPtr handle)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = access };
        return NativeMethods.OpenPrinter(name, out handle, ref defaults);
    }

    internal static int HResultFromWin32(int code) =>
        code <= 0 ? code : unchecked((int)((uint)(code & 0x0000FFFF) | 0x80070000));

    internal static Win32Exception LastError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation} fallo con Win32 {code}: {new Win32Exception(code).Message}");
    }
}
