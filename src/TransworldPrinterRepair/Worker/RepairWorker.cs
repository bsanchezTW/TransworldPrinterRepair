using Transworld.PrinterRepair.Infrastructure;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Worker;

/// <summary>
/// Punto de entrada del proceso elevado. No muestra ninguna ventana: hace el trabajo,
/// informa del progreso por el pipe y termina. No deja nada residente.
///
/// El codigo de salida duplica el resultado (0 correcto, o el valor de RepairErrorCode)
/// para que la UI sepa que paso incluso si el canal de progreso se corto.
/// </summary>
public static class RepairWorker
{
    public static int Run(string? areaKey, string? pipeName)
    {
        var log = new LoggingService("worker");
        log.Banner($"Inicio de reparacion | area='{areaKey}' | elevado={SystemChecks.IsElevated()}");

        using var progress = new ProgressClient(pipeName, log);

        try
        {
            if (string.IsNullOrWhiteSpace(areaKey))
                throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                    "No se indico el area a reparar.");

            if (!SystemChecks.IsElevated())
                throw new RepairException(RepairErrorCode.PermisosInsuficientes,
                    "El worker se inicio sin privilegios de administrador.");

            var configuration = new ConfigurationService(log);
            var config = configuration.Load();

            var area = config.FindArea(areaKey)
                ?? throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                       $"El area '{areaKey}' no existe en la configuracion.");

            var printers = new PrinterApi(log);
            var ports = new PortApi(log);
            var spooler = new SpoolerControl(log);
            var runner = new ProcessRunner(log);

            var service = new PrinterRepairService(
                printers,
                ports,
                spooler,
                new PrinterDiscoveryService(printers, log),
                new DriverService(printers, runner, log),
                new NetworkService(log),
                log);

            var result = service.Repair(config, area, progress.Send);

            progress.Send(new ProgressMessage
            {
                Kind = "result",
                Success = result.Success,
                ErrorCode = result.ErrorCode,
                ErrorDetail = result.ErrorDetail,
                PrinterName = result.PrinterName,
                PortName = result.PortName,
            });

            log.Info($"Worker finalizado. Exito={result.Success} Codigo={result.ErrorCode}");
            return result.Success ? 0 : (int)result.ErrorCode;
        }
        catch (RepairException ex)
        {
            log.Error("Worker abortado: " + ex.Message, ex);
            progress.Send(new ProgressMessage
            {
                Kind = "result",
                Success = false,
                ErrorCode = ex.Code,
                ErrorDetail = ex.Message,
            });
            return (int)ex.Code;
        }
        catch (Exception ex)
        {
            log.Error("Worker abortado por un error inesperado.", ex);
            progress.Send(new ProgressMessage
            {
                Kind = "result",
                Success = false,
                ErrorCode = RepairErrorCode.ErrorInesperado,
                ErrorDetail = LoggingService.Describe(ex),
            });
            return (int)RepairErrorCode.ErrorInesperado;
        }
    }
}
