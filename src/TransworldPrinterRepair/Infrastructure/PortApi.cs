using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Transworld.PrinterRepair.Infrastructure.Interop;
using Transworld.PrinterRepair.Services;
using static Transworld.PrinterRepair.Infrastructure.Interop.NativeMethods;

namespace Transworld.PrinterRepair.Infrastructure;

/// <summary>Configuracion de un puerto "Standard TCP/IP Port".</summary>
public sealed record TcpPortInfo(
    string Name,
    string HostAddress,
    int PortNumber,
    int Protocol,
    bool SnmpEnabled)
{
    public bool IsRaw => Protocol == (int)PROTOCOL_RAWTCP_TYPE;

    /// <summary>El puerto ya apunta a esta IP, sin importar como se llame.</summary>
    public bool PointsTo(string ip) =>
        string.Equals(HostAddress, ip, StringComparison.OrdinalIgnoreCase);

    /// <summary>Configuracion deseada: RAW 9100 con SNMP desactivado.</summary>
    public bool MatchesDesired(string ip, int port) =>
        PointsTo(ip) && PortNumber == port && IsRaw && !SnmpEnabled;
}

/// <summary>
/// Crea y repara puertos TCP/IP.
///
/// Lectura: del registro del monitor. Es la fuente exacta que usa Windows y evita depender
/// del formato de salida de XcvData GetConfig, que no esta documentado de forma estable.
/// Escritura: XcvData AddPort / ConfigPort / DeletePort, que si son la via soportada.
/// </summary>
public sealed class PortApi
{
    private const string PortsKey =
        @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports";

    /// <summary>RAW en 9100 es el estandar de facto y mas fiable que LPR para estas impresoras.</summary>
    public const int RawPort = 9100;

    private readonly LoggingService _log;

    public PortApi(LoggingService log) => _log = log;

    /// <summary>Nombre canonico. Usar siempre el mismo evita IP_x_2, IP_x_3, etc.</summary>
    public static string CanonicalName(string ip) => "IP_" + ip;

    // =====================================================================
    // Lectura
    // =====================================================================

    public IReadOnlyList<TcpPortInfo> ListTcpPorts()
    {
        var result = new List<TcpPortInfo>();

        using var root = Registry.LocalMachine.OpenSubKey(PortsKey);
        if (root is null) return result;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;

            // Windows guarda la direccion en HostName; IPAddress suele quedar vacio
            // cuando el puerto se creo indicando directamente una IP.
            var host = key.GetValue("HostName") as string;
            if (string.IsNullOrWhiteSpace(host))
                host = key.GetValue("IPAddress") as string;

            result.Add(new TcpPortInfo(
                name,
                (host ?? string.Empty).Trim(),
                Convert.ToInt32(key.GetValue("PortNumber") ?? 0),
                Convert.ToInt32(key.GetValue("Protocol") ?? 0),
                Convert.ToInt32(key.GetValue("SNMP Enabled") ?? 0) != 0));
        }

        return result;
    }

    public TcpPortInfo? FindByName(string portName) =>
        ListTcpPorts().FirstOrDefault(p =>
            string.Equals(p.Name, portName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Todos los puertos que ya apuntan a esta IP, se llamen como se llamen.</summary>
    public IReadOnlyList<TcpPortInfo> FindByAddress(string ip) =>
        ListTcpPorts().Where(p => p.PointsTo(ip)).ToList();

    /// <summary>Nombres de todos los puertos del spooler, para el log de verificacion.</summary>
    public IReadOnlyList<string> ListAllPortNames()
    {
        EnumPorts(null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPorts(null, 2, buffer, needed, out _, out var count))
                throw PrinterApi.LastError("EnumPorts");

            var names = new List<string>((int)count);
            var size = Marshal.SizeOf<PORT_INFO_2>();

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PORT_INFO_2>(buffer + i * size);
                if (!string.IsNullOrEmpty(item.pPortName)) names.Add(item.pPortName!);
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // =====================================================================
    // Escritura (XcvData)
    // =====================================================================

    /// <summary>
    /// Deja disponible un puerto RAW 9100 hacia la IP indicada y devuelve su nombre.
    ///
    /// Idempotente: reutiliza cualquier puerto que ya apunte a esa IP, lo reconfigura si
    /// esta mal, y solo crea uno nuevo si no existe ninguno. Es lo que impide que se
    /// acumulen IP_192.168.190.8, IP_192.168.190.8_1, _2...
    /// </summary>
    public string EnsurePort(string ip)
    {
        if (!Validation.IsUsablePrinterIp(ip))
            throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                "IP no valida para crear el puerto: " + ip);

        var existing = FindByAddress(ip);

        if (existing.Count > 0)
        {
            // Se prefiere el que ya tenga el nombre canonico; si no, el primero.
            var canonical = CanonicalName(ip);
            var chosen = existing.FirstOrDefault(p =>
                string.Equals(p.Name, canonical, StringComparison.OrdinalIgnoreCase)) ?? existing[0];

            if (chosen.MatchesDesired(ip, RawPort))
            {
                _log.Info($"Puerto {chosen.Name} ya apunta a {ip} en RAW {RawPort}; se reutiliza.");
                return chosen.Name;
            }

            _log.Info($"Puerto {chosen.Name} apunta a {ip} pero esta como " +
                      $"protocolo {chosen.Protocol} puerto {chosen.PortNumber} " +
                      $"SNMP={chosen.SnmpEnabled}; se reconfigura a RAW {RawPort}.");

            Configure(chosen.Name, ip, "ConfigPort");
            return chosen.Name;
        }

        var name = CanonicalName(ip);

        // Puede existir un puerto con ese nombre apuntando a otra direccion.
        if (FindByName(name) is not null)
        {
            _log.Info($"Existe un puerto {name} con otra direccion; se reconfigura hacia {ip}.");
            Configure(name, ip, "ConfigPort");
            return name;
        }

        _log.Info($"Creando puerto {name} -> {ip}:{RawPort} RAW, SNMP desactivado.");
        Configure(name, ip, "AddPort");
        return name;
    }

    /// <summary>
    /// Elimina puertos TCP/IP que ya no usa ninguna impresora. Debe llamarse DESPUES de
    /// borrar las colas: un puerto en uso no se puede eliminar.
    /// </summary>
    public int DeleteUnusedPorts(IEnumerable<string> portNames, ISet<string> portsInUse)
    {
        var deleted = 0;

        foreach (var name in portNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (portsInUse.Contains(name))
            {
                _log.Info($"Puerto {name} sigue en uso; no se elimina.");
                continue;
            }

            try
            {
                DeletePort(name);
                _log.Info($"Puerto huerfano eliminado: {name}");
                deleted++;
            }
            catch (RepairException ex) when (ex.Win32Code == ERROR_BUSY)
            {
                // Normal: el spooler lo tiene fijado desde que lo abrio y no lo suelta hasta
                // reiniciarse. No es un fallo; si apunta a la IP correcta se reutiliza tal cual.
                _log.Info($"El puerto {name} sigue fijado por el spooler; se reutilizara en vez de recrearlo.");
            }
            catch (Exception ex)
            {
                // Un puerto que no se deja borrar no impide completar la reparacion.
                _log.Warn($"No se pudo eliminar el puerto {name}: {LoggingService.Describe(ex)}");
            }
        }

        return deleted;
    }

    private void Configure(string portName, string ip, string operation)
    {
        var data = new PORT_DATA_1
        {
            sztPortName = portName,
            dwVersion = 1,
            dwProtocol = PROTOCOL_RAWTCP_TYPE,
            cbSize = (uint)Marshal.SizeOf<PORT_DATA_1>(),
            dwReserved = 0,
            sztHostAddress = ip,
            sztSNMPCommunity = "public",
            dwDoubleSpool = 0,
            sztQueue = string.Empty,
            sztIPAddress = string.Empty,
            Reserved = new byte[540],
            dwPortNumber = RawPort,

            // SNMP desactivado a proposito: con SNMP activo Windows marca la impresora como
            // "Sin conexion" en cuanto una consulta falla, aunque la impresora imprima bien.
            // Es la causa mas habitual de la averia que este programa viene a reparar.
            dwSNMPEnabled = 0,
            dwSNMPDevIndex = 1,
        };

        var size = Marshal.SizeOf<PORT_DATA_1>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(data, buffer, false);
            ExecuteXcv(operation, buffer, (uint)size);
        }
        finally
        {
            Marshal.DestroyStructure<PORT_DATA_1>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// El monitor TCP/IP espera una estructura DELETE_PORT_DATA_1, no el nombre suelto:
    /// pasarle una cadena se rechaza con ERROR_INVALID_DATA (13).
    ///
    /// Un puerto que el spooler ha llegado a abrir queda fijado hasta que se reinicia el
    /// servicio: ni esta API ni Remove-PrinterPort consiguen borrarlo, por mucho que se espere
    /// (medido: sigue ocupado pasados 22 segundos sin ninguna impresora usandolo). Por eso el
    /// reintento es corto, solo para casos realmente transitorios, y quedarse ocupado no se
    /// trata como un error: el paso siguiente reutiliza ese puerto y lo reconfigura.
    ///
    /// En la practica la limpieza SI funciona en el caso que importa, que es el de los puertos
    /// duplicados heredados de sesiones anteriores: tras arrancar el equipo el spooler no los
    /// tiene abiertos y se eliminan sin problema.
    ///
    /// NO se recurre a DeletePortW como alternativa: esa API abre un dialogo de Windows,
    /// y este proceso corre sin supervision detras de la pantalla de progreso.
    /// </summary>
    private void DeletePort(string portName)
    {
        const int maxAttempts = 4;
        const int retryDelayMs = 400;

        var data = new DELETE_PORT_DATA_1
        {
            psztPortName = portName,
            Reserved = new byte[98],
            dwVersion = 1,
            dwReserved = 0,
        };

        var size = Marshal.SizeOf<DELETE_PORT_DATA_1>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(data, buffer, false);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    ExecuteXcv("DeletePort", buffer, (uint)size);
                    return;
                }
                catch (RepairException ex) when (ex.Win32Code == ERROR_BUSY && attempt < maxAttempts)
                {
                    _log.Info($"El puerto {portName} sigue ocupado; reintento {attempt} de {maxAttempts - 1}.");
                    Thread.Sleep(retryDelayMs);
                }
            }
        }
        finally
        {
            Marshal.DestroyStructure<DELETE_PORT_DATA_1>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Abre el monitor TCP/IP y ejecuta una operacion XcvData.</summary>
    private void ExecuteXcv(string operation, IntPtr input, uint inputSize)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = SERVER_ACCESS_ADMINISTER };

        if (!OpenPrinter(XcvMonitorTcpIp, out var handle, ref defaults))
        {
            var code = Marshal.GetLastWin32Error();
            throw new RepairException(
                code == ERROR_ACCESS_DENIED
                    ? RepairErrorCode.PermisosInsuficientes
                    : RepairErrorCode.PuertoNoConfigurado,
                $"No se pudo abrir el monitor TCP/IP (Win32 {code}).",
                new Win32Exception(code));
        }

        try
        {
            var ok = XcvData(handle, operation, input, inputSize,
                IntPtr.Zero, 0, out _, out var status);

            if (!ok)
            {
                var code = Marshal.GetLastWin32Error();
                throw new RepairException(RepairErrorCode.PuertoNoConfigurado,
                    $"XcvData({operation}) fallo (Win32 {code}).", new Win32Exception(code))
                {
                    Win32Code = code,
                };
            }

            // XcvData devuelve TRUE aunque la operacion falle: el resultado real va en status.
            if (status != 0)
            {
                throw new RepairException(
                    status == ERROR_ACCESS_DENIED
                        ? RepairErrorCode.PermisosInsuficientes
                        : RepairErrorCode.PuertoNoConfigurado,
                    $"XcvData({operation}) devolvio estado {status}.",
                    new Win32Exception((int)status))
                {
                    Win32Code = (int)status,
                };
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }
}
