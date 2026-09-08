using System.IO;
using System.Text;
using Transworld.PrinterRepair.Infrastructure;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Worker;

/// <summary>
/// Autodiagnostico no destructivo. Comprueba el entorno y valida que las llamadas nativas
/// funcionan, creando y eliminando un puerto y una impresora TEMPORALES propios.
///
/// Nunca toca las impresoras ni los puertos existentes del equipo. Sirve para verificar un
/// PC antes de desplegar y para diagnosticar cuando una reparacion falla en campo.
/// </summary>
public static class SelfTest
{
    /// <summary>
    /// IP de la red 192.0.2.0/24 (TEST-NET-1, RFC 5737), reservada para documentacion.
    /// Nunca corresponde a un equipo real, asi que la prueba no puede interferir con nadie.
    /// </summary>
    private const string ProbeIp = "192.0.2.250";

    private const string ProbePrinter = "ZZ PRUEBA TRANSWORLD";

    public static int Run()
    {
        var log = new LoggingService("selftest");
        var report = new StringBuilder();
        var failures = 0;

        void Check(string name, Func<string> action)
        {
            try
            {
                var detail = action();
                report.AppendLine($"[ OK ] {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"[FALLO] {name} :: {LoggingService.Describe(ex)}");
                log.Error("Autodiagnostico: " + name, ex);
            }
        }

        report.AppendLine("AUTODIAGNOSTICO - AutoReparacion de Impresoras Transworld");
        report.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine(new string('=', 70));
        report.AppendLine("Sistema  : " + SystemChecks.DescribeWindows());
        report.AppendLine("Elevado  : " + SystemChecks.IsElevated());
        report.AppendLine("Protected Print Mode: " + SystemChecks.IsProtectedPrintModeEnabled());
        report.AppendLine("Datos    : " + AppPaths.Root);
        report.AppendLine();

        var printers = new PrinterApi(log);
        var ports = new PortApi(log);
        var spooler = new SpoolerControl(log);
        var configuration = new ConfigurationService(log);

        AppConfig? config = null;

        Check("Cargar configuracion embebida", () =>
        {
            config = configuration.Load();
            return $"{config.Areas.Count} areas, {config.DriverPackages.Count} paquetes";
        });

        Check("Servicio de spooler activo", () =>
            spooler.IsRunning() ? "en ejecucion" : throw new Exception("el spooler no esta en ejecucion"));

        Check("Enumerar impresoras (EnumPrinters nivel 2)", () =>
        {
            var all = printers.ListPrinters();
            foreach (var p in all)
                report.AppendLine($"         - {p.Name} | driver={p.DriverName} | puerto={p.PortName}");
            return $"{all.Count} encontradas";
        });

        if (config is not null)
        {
            Check("Clasificar impresoras (fisicas vs virtuales)", () =>
            {
                var inventory = new PrinterDiscoveryService(printers, log).Classify(config!);
                return $"{inventory.ToRemove.Count} se eliminarian, {inventory.ToKeep.Count} se conservarian";
            });
        }

        Check("Enumerar puertos TCP/IP (registro del monitor)", () =>
        {
            var all = ports.ListTcpPorts();
            foreach (var p in all)
                report.AppendLine($"         - {p.Name} -> {p.HostAddress}:{p.PortNumber} " +
                                  $"protocolo={p.Protocol} snmp={p.SnmpEnabled}");
            return $"{all.Count} encontrados";
        });

        Check("Enumerar drivers instalados (EnumPrinterDrivers)", () =>
        {
            var installed = printers.ListInstalledDriverNames();

            if (config is not null)
            {
                foreach (var pkg in config.DriverPackages)
                {
                    var present = installed.Any(n =>
                        string.Equals(n, pkg.Value.DriverName, StringComparison.OrdinalIgnoreCase));
                    report.AppendLine($"         - {pkg.Key}: '{pkg.Value.DriverName}' " +
                                      (present ? "YA INSTALADO" : "se instalaria"));
                }
            }

            return $"{installed.Count} drivers en el spooler";
        });

        // ---- Prueba de ida y vuelta: solo con privilegios de administrador ----

        if (!SystemChecks.IsElevated())
        {
            report.AppendLine();
            report.AppendLine("[OMITIDO] Prueba de puerto e impresora: requiere ejecutar como administrador.");
        }
        else
        {
            report.AppendLine();
            report.AppendLine($"--- Prueba de ida y vuelta sobre {ProbeIp} (IP reservada RFC 5737) ---");

            var probePort = string.Empty;
            var driverForProbe = string.Empty;

            Check($"Crear puerto TCP/IP para {ProbeIp} (XcvData AddPort)", () =>
            {
                probePort = ports.EnsurePort(ProbeIp);
                return probePort;
            });

            Check("Releer la configuracion del puerto creado", () =>
            {
                var port = ports.FindByName(probePort)
                    ?? throw new Exception("el puerto no aparece tras crearlo");

                if (!port.MatchesDesired(ProbeIp, PortApi.RawPort))
                    throw new Exception($"quedo como {port.HostAddress}:{port.PortNumber} " +
                                        $"protocolo={port.Protocol} snmp={port.SnmpEnabled}");

                return $"{port.HostAddress}:{port.PortNumber} RAW, SNMP desactivado";
            });

            Check("Idempotencia: repetir EnsurePort no duplica", () =>
            {
                var again = ports.EnsurePort(ProbeIp);
                var count = ports.FindByAddress(ProbeIp).Count;

                if (count != 1) throw new Exception($"hay {count} puertos apuntando a {ProbeIp}");
                if (again != probePort) throw new Exception($"devolvio '{again}' en vez de '{probePort}'");

                return "sigue habiendo exactamente 1 puerto";
            });

            Check("Crear impresora de prueba (AddPrinter nivel 2)", () =>
            {
                // Se reutiliza un driver ya presente: la prueba valida AddPrinter,
                // no la instalacion del controlador.
                driverForProbe = printers.ListInstalledDriverNames().FirstOrDefault()
                    ?? throw new Exception("no hay ningun driver instalado que reutilizar");

                printers.AddPrinter(ProbePrinter, probePort, driverForProbe);
                return $"{ProbePrinter} con driver '{driverForProbe}'";
            });

            Check("Releer la impresora de prueba (GetPrinter)", () =>
            {
                var printer = printers.GetPrinter(ProbePrinter)
                    ?? throw new Exception("no aparece tras crearla");

                if (!string.Equals(printer.PortName, probePort, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"quedo en el puerto '{printer.PortName}'");

                return $"puerto={printer.PortName} driver={printer.DriverName}";
            });

            // ---- Limpieza: la prueba no deja nada detras ----

            Check("Eliminar la impresora de prueba", () =>
            {
                printers.DeletePrinter(ProbePrinter);
                return printers.GetPrinter(ProbePrinter) is null
                    ? "eliminada"
                    : throw new Exception("sigue existiendo");
            });

            Check("Eliminar el puerto de prueba", () =>
            {
                var inUse = new HashSet<string>(
                    printers.ListPrinters().Select(p => p.PortName), StringComparer.OrdinalIgnoreCase);

                var deleted = ports.DeleteUnusedPorts(new[] { probePort }, inUse);

                return ports.FindByName(probePort) is null
                    ? $"eliminado ({deleted})"
                    : throw new Exception("el puerto sigue existiendo");
            });
        }

        report.AppendLine();
        report.AppendLine(new string('=', 70));
        report.AppendLine(failures == 0
            ? "RESULTADO: todas las comprobaciones correctas."
            : $"RESULTADO: {failures} comprobaciones fallidas.");

        AppPaths.TryEnsureDirectories();
        var path = Path.Combine(AppPaths.LogsDirectory,
            $"autodiagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        try
        {
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
            log.Info("Autodiagnostico escrito en " + path);
        }
        catch (Exception ex)
        {
            log.Error("No se pudo escribir el informe de autodiagnostico.", ex);
        }

        return failures;
    }
}
