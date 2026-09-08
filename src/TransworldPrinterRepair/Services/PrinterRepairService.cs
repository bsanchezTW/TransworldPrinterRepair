using Transworld.PrinterRepair.Infrastructure;

namespace Transworld.PrinterRepair.Services;

/// <summary>
/// Orquesta la reparacion completa. Corre siempre dentro del worker elevado y no conoce
/// nada de la interfaz: solo emite mensajes de progreso que alguien mas decide como pintar.
///
/// Toda la secuencia es idempotente: ejecutarla N veces deja el equipo en el mismo estado.
/// </summary>
public sealed class PrinterRepairService
{
    private readonly PrinterApi _printers;
    private readonly PortApi _ports;
    private readonly SpoolerControl _spooler;
    private readonly PrinterDiscoveryService _discovery;
    private readonly DriverService _drivers;
    private readonly NetworkService _network;
    private readonly LoggingService _log;

    public PrinterRepairService(
        PrinterApi printers,
        PortApi ports,
        SpoolerControl spooler,
        PrinterDiscoveryService discovery,
        DriverService drivers,
        NetworkService network,
        LoggingService log)
    {
        _printers = printers;
        _ports = ports;
        _spooler = spooler;
        _discovery = discovery;
        _drivers = drivers;
        _network = network;
        _log = log;
    }

    public RepairResult Repair(AppConfig config, AreaDefinition area, Action<ProgressMessage> report)
    {
        var printerName = area.PrinterName;
        var portName = string.Empty;

        try
        {
            var package = config.FindPackage(area)
                ?? throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                       $"El area '{area.Name}' referencia el paquete inexistente '{area.DriverPackage}'.");

            if (!Validation.IsUsablePrinterIp(area.Ip))
                throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                    $"La IP configurada para '{area.Name}' no es valida: '{area.Ip}'.");

            _log.Info($"Area: {area.Name} | IP: {area.Ip} | Impresora: {printerName}");
            _log.Info($"Paquete: {package.Model} | Driver: {package.DriverName}");

            // ---- 1. Preparar sistema -------------------------------------
            report(ProgressMessage.ForStep(RepairStep.Preparar, StepState.Running));
            _log.Info("Sistema: " + SystemChecks.DescribeWindows());
            _log.Info("Proceso elevado: " + SystemChecks.IsElevated());
            _spooler.EnsureRunning();
            report(ProgressMessage.ForStep(RepairStep.Preparar, StepState.Ok,
                "Servicio de impresion activo"));

            // ---- 2. Detectar impresoras ----------------------------------
            report(ProgressMessage.ForStep(RepairStep.Detectar, StepState.Running));
            var inventory = _discovery.Classify(config);
            report(ProgressMessage.ForStep(RepairStep.Detectar, StepState.Ok,
                $"{inventory.ToRemove.Count + inventory.ToKeep.Count} impresoras detectadas"));

            // ---- 3. Eliminar impresoras existentes -----------------------
            report(ProgressMessage.ForStep(RepairStep.EliminarImpresoras, StepState.Running));
            var freedPorts = RemovePrinters(inventory.ToRemove, out var removed, out var failed);

            if (failed > 0)
            {
                // Una cola bloqueada suele desatascarse reiniciando el spooler.
                _log.Warn($"{failed} impresoras no se pudieron eliminar; se reinicia el spooler.");
                _spooler.RestartClearingQueue();

                var pending = _discovery.Classify(config).ToRemove;
                var freedOnRetry = RemovePrinters(pending, out var removedOnRetry, out failed);

                foreach (var port in freedOnRetry) freedPorts.Add(port);
                removed += removedOnRetry;
            }

            report(ProgressMessage.ForStep(RepairStep.EliminarImpresoras,
                failed > 0 ? StepState.Warning : StepState.Ok,
                $"{removed} impresoras eliminadas" + (failed > 0 ? $", {failed} resistieron" : "")));

            // ---- 4. Limpiar puertos huerfanos ----------------------------
            report(ProgressMessage.ForStep(RepairStep.LimpiarPuertos, StepState.Running));
            var deletedPorts = CleanUpPorts(area.Ip, freedPorts);
            report(ProgressMessage.ForStep(RepairStep.LimpiarPuertos, StepState.Ok,
                deletedPorts == 0 ? "Sin puertos sobrantes" : $"{deletedPorts} puertos sobrantes eliminados"));

            // ---- 5-7. Controlador ----------------------------------------
            report(ProgressMessage.ForStep(RepairStep.ExtraerDriver, StepState.Running));
            var plan = _drivers.ResolvePlan(package);
            var infPath = plan.Kind == DriverPlanKind.InstallVendor
                ? _drivers.ExtractPackage(package)
                : null;
            report(ProgressMessage.ForStep(RepairStep.ExtraerDriver, StepState.Ok, plan.Reason));

            report(ProgressMessage.ForStep(RepairStep.InstalarDriver, StepState.Running));
            if (infPath is not null) _drivers.StageInDriverStore(infPath);
            report(ProgressMessage.ForStep(RepairStep.InstalarDriver, StepState.Ok,
                infPath is null ? "No hizo falta instalar nada" : "Controlador almacenado en Windows"));

            report(ProgressMessage.ForStep(RepairStep.PublicarDriver, StepState.Running));
            if (infPath is not null)
            {
                _drivers.PublishToSpooler(infPath, plan.DriverName);
                _drivers.VerifyInstalled(plan.DriverName);
            }
            report(ProgressMessage.ForStep(RepairStep.PublicarDriver, StepState.Ok, plan.DriverName));

            // ---- 8. Puerto TCP/IP ----------------------------------------
            report(ProgressMessage.ForStep(RepairStep.ConfigurarPuerto, StepState.Running));
            portName = _ports.EnsurePort(area.Ip);
            report(ProgressMessage.ForStep(RepairStep.ConfigurarPuerto, StepState.Ok,
                $"{portName} ({area.Ip}:{PortApi.RawPort})"));

            // ---- 9. Crear la impresora -----------------------------------
            report(ProgressMessage.ForStep(RepairStep.CrearImpresora, StepState.Running));
            CreatePrinter(printerName, portName, plan.DriverName);
            report(ProgressMessage.ForStep(RepairStep.CrearImpresora, StepState.Ok, printerName));

            // ---- 10. Verificar comunicacion ------------------------------
            report(ProgressMessage.ForStep(RepairStep.VerificarConexion, StepState.Running));
            var reachable = _network.CanReach(area.Ip, PortApi.RawPort);

            if (!reachable)
            {
                report(ProgressMessage.ForStep(RepairStep.VerificarConexion, StepState.Failed,
                    $"Sin respuesta de {area.Ip}"));

                // La impresora queda configurada; solo no contesta. Reintentar es inmediato.
                return RepairResult.Fail(RepairErrorCode.ImpresoraNoResponde,
                    $"No hubo respuesta TCP en {area.Ip}:{PortApi.RawPort}.", printerName);
            }

            report(ProgressMessage.ForStep(RepairStep.VerificarConexion, StepState.Ok,
                $"{area.Ip} responde"));

            // ---- 11. Verificar configuracion -----------------------------
            report(ProgressMessage.ForStep(RepairStep.VerificarConfiguracion, StepState.Running));
            VerifyFinalState(printerName, portName, plan.DriverName, area.Ip);
            report(ProgressMessage.ForStep(RepairStep.VerificarConfiguracion, StepState.Ok,
                "Configuracion correcta"));

            _log.Info("Reparacion completada correctamente.");
            return RepairResult.Ok(printerName, portName);
        }
        catch (RepairException ex)
        {
            _log.Error("Reparacion fallida: " + ex.Message, ex);
            return RepairResult.Fail(ex.Code, ex.Message, printerName);
        }
        catch (Exception ex)
        {
            _log.Error("Error inesperado durante la reparacion.", ex);
            return RepairResult.Fail(RepairErrorCode.ErrorInesperado, LoggingService.Describe(ex), printerName);
        }
    }

    // =====================================================================

    /// <summary>Elimina las colas indicadas y devuelve los puertos que quedan libres.</summary>
    private HashSet<string> RemovePrinters(
        IReadOnlyList<PrinterInfo> printers, out int removed, out int failed)
    {
        var freed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        removed = 0;
        failed = 0;

        foreach (var printer in printers)
        {
            try
            {
                _printers.DeletePrinter(printer.Name);
                if (!string.IsNullOrWhiteSpace(printer.PortName)) freed.Add(printer.PortName);
                removed++;
                _log.Info($"Impresora eliminada: {printer.Name}");
            }
            catch (Exception ex)
            {
                // Las impresoras conectadas de un servidor se quitan de otra forma.
                if (printer.IsNetwork && _printers.TryDeletePrinterConnection(printer.Name))
                {
                    removed++;
                    _log.Info($"Conexion de red eliminada: {printer.Name}");
                    continue;
                }

                failed++;
                _log.Error($"No se pudo eliminar {printer.Name}", ex);
            }
        }

        return freed;
    }

    /// <summary>
    /// Borra los puertos TCP/IP que quedaron sin duenno y los duplicados de la IP objetivo.
    /// Se conserva el puerto con el nombre canonico, que el paso siguiente reconfigura.
    /// Nunca se tocan puertos que no sean Standard TCP/IP (COM, LPT, PORTPROMPT, OneNote).
    /// </summary>
    private int CleanUpPorts(string targetIp, ISet<string> freedPorts)
    {
        var canonical = PortApi.CanonicalName(targetIp);

        var inUse = new HashSet<string>(
            _printers.ListPrinters().Select(p => p.PortName),
            StringComparer.OrdinalIgnoreCase);

        var candidates = _ports.ListTcpPorts()
            .Where(p => !string.Equals(p.Name, canonical, StringComparison.OrdinalIgnoreCase))
            .Where(p => freedPorts.Contains(p.Name) || p.PointsTo(targetIp))
            .Select(p => p.Name)
            .ToList();

        if (candidates.Count == 0) return 0;

        _log.Info("Puertos candidatos a limpieza: " + string.Join(", ", candidates));
        return _ports.DeleteUnusedPorts(candidates, inUse);
    }

    /// <summary>
    /// Crea la impresora. Si ya existe una con ese nombre se elimina primero: es lo que
    /// evita que Windows genere "IMPRESORA CONTABILIDAD (2)" al repetir la reparacion.
    /// </summary>
    private void CreatePrinter(string printerName, string portName, string driverName)
    {
        var existing = _printers.GetPrinter(printerName);

        if (existing is not null)
        {
            _log.Info($"Ya existe {printerName}; se elimina para recrearla con la configuracion correcta.");
            _printers.DeletePrinter(printerName);
        }

        _printers.AddPrinter(printerName, portName, driverName);
        _log.Info($"Impresora creada: {printerName} -> {portName} [{driverName}]");
    }

    /// <summary>Relee del sistema lo que se acaba de configurar y comprueba que cuadra.</summary>
    private void VerifyFinalState(string printerName, string portName, string driverName, string ip)
    {
        var printer = _printers.GetPrinter(printerName)
            ?? throw new RepairException(RepairErrorCode.VerificacionFallida,
                   $"La impresora '{printerName}' no aparece en el sistema tras crearla.");

        if (!string.Equals(printer.PortName, portName, StringComparison.OrdinalIgnoreCase))
            throw new RepairException(RepairErrorCode.VerificacionFallida,
                $"'{printerName}' quedo en el puerto '{printer.PortName}' en vez de '{portName}'.");

        if (!string.Equals(printer.DriverName, driverName, StringComparison.OrdinalIgnoreCase))
            throw new RepairException(RepairErrorCode.VerificacionFallida,
                $"'{printerName}' quedo con el driver '{printer.DriverName}' en vez de '{driverName}'.");

        var port = _ports.FindByName(portName)
            ?? throw new RepairException(RepairErrorCode.VerificacionFallida,
                   $"El puerto '{portName}' no aparece en el sistema.");

        if (!port.MatchesDesired(ip, PortApi.RawPort))
            throw new RepairException(RepairErrorCode.VerificacionFallida,
                $"El puerto '{portName}' apunta a '{port.HostAddress}:{port.PortNumber}' " +
                $"(protocolo {port.Protocol}, SNMP {port.SnmpEnabled}) en vez de " +
                $"'{ip}:{PortApi.RawPort}' RAW sin SNMP.");

        if (!_spooler.IsRunning())
            throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
                "El spooler no esta en ejecucion al terminar.");

        // Deja constancia del estado final para el diagnostico posterior.
        _log.Info($"Verificado: {printer.Name} | puerto {printer.PortName} | driver {printer.DriverName}");
        _log.Info("Puertos del sistema: " + string.Join(", ", _ports.ListAllPortNames()));
    }
}
