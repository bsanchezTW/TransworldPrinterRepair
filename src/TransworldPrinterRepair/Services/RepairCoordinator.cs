using System.Diagnostics;
using Microsoft.Win32;
using Transworld.PrinterRepair.Infrastructure;

namespace Transworld.PrinterRepair.Services;

/// <summary>
/// Lado NO elevado de la reparacion: pide la elevacion, escucha el progreso del worker y,
/// cuando termina bien, aplica los ajustes que son por usuario.
/// </summary>
public sealed class RepairCoordinator
{
    private readonly ElevatedLauncher _launcher;
    private readonly PrinterApi _printers;
    private readonly LoggingService _log;

    public RepairCoordinator(ElevatedLauncher launcher, PrinterApi printers, LoggingService log)
    {
        _launcher = launcher;
        _printers = printers;
        _log = log;
    }

    public async Task<RepairResult> RunAsync(
        AreaDefinition area,
        Action<ProgressMessage> onProgress,
        CancellationToken cancellationToken = default)
    {
        _log.Banner($"Inicio de reparacion desde la interfaz | area='{area.Name}' | ip={area.Ip}");

        using var server = new ProgressServer();
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        RepairResult? reported = null;

        var receiving = server.ReceiveAsync(message =>
        {
            if (message.Kind == "result")
            {
                reported = message.Success
                    ? RepairResult.Ok(message.PrinterName ?? area.PrinterName, message.PortName ?? string.Empty)
                    : RepairResult.Fail(message.ErrorCode, message.ErrorDetail, message.PrinterName);
            }

            onProgress(message);
        }, drain.Token);

        Process? worker;
        try
        {
            worker = _launcher.Launch(area.Key, server.PipeName);
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo lanzar el proceso elevado.", ex);
            drain.Cancel();
            await Swallow(receiving).ConfigureAwait(false);
            return RepairResult.Fail(RepairErrorCode.ErrorInesperado, LoggingService.Describe(ex));
        }

        if (worker is null)
        {
            drain.Cancel();
            await Swallow(receiving).ConfigureAwait(false);
            return RepairResult.Fail(RepairErrorCode.UacCancelado, "El usuario cancelo el dialogo UAC.");
        }

        using (worker)
        {
            await worker.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Margen para que lleguen los ultimos mensajes antes de cerrar el canal.
            drain.CancelAfter(TimeSpan.FromSeconds(2));
            await Swallow(receiving).ConfigureAwait(false);

            var exitCode = worker.ExitCode;
            _log.Info($"El worker termino con codigo {exitCode}.");

            var result = reported ?? FromExitCode(exitCode, area);

            if (result.Success && result.PrinterName is not null)
                ApplyUserScopedSettings(result.PrinterName);

            return result;
        }
    }

    /// <summary>
    /// Ajustes que pertenecen al perfil del usuario y por eso NO puede hacer el worker:
    /// si el UAC se acepto con la cuenta de otro administrador, el worker los aplicaria
    /// a ese perfil y el trabajador no veria ningun cambio.
    /// </summary>
    private void ApplyUserScopedSettings(string printerName)
    {
        _printers.TrySetDefaultPrinter(printerName);
        DisableWindowsManagedDefault();
    }

    /// <summary>
    /// Desactiva "Permitir que Windows administre mi impresora predeterminada". Sin esto,
    /// Windows vuelve a cambiar la predeterminada a la ultima usada y el usuario acaba
    /// llamando otra vez a soporte.
    /// </summary>
    private void DisableWindowsManagedDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");

            key?.SetValue("LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord);
            _log.Info("Desactivada la gestion automatica de impresora predeterminada de Windows.");
        }
        catch (Exception ex)
        {
            _log.Warn("No se pudo desactivar la gestion automatica de la predeterminada: "
                      + LoggingService.Describe(ex));
        }
    }

    /// <summary>Red de seguridad si el canal de progreso se corto antes del mensaje final.</summary>
    private RepairResult FromExitCode(int exitCode, AreaDefinition area)
    {
        if (exitCode == 0)
        {
            _log.Warn("El worker termino correctamente pero no llego el mensaje de resultado.");
            return RepairResult.Ok(area.PrinterName, string.Empty);
        }

        var code = Enum.IsDefined(typeof(RepairErrorCode), exitCode)
            ? (RepairErrorCode)exitCode
            : RepairErrorCode.ErrorInesperado;

        return RepairResult.Fail(code, $"El worker termino con codigo {exitCode}.", area.PrinterName);
    }

    private static async Task Swallow(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }
}
