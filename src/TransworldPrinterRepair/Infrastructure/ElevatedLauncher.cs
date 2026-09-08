using System.ComponentModel;
using System.Diagnostics;
using Transworld.PrinterRepair.Services;
using static Transworld.PrinterRepair.Infrastructure.Interop.NativeMethods;

namespace Transworld.PrinterRepair.Infrastructure;

/// <summary>
/// Lanza este mismo ejecutable en modo worker con privilegios de administrador.
///
/// La aplicacion arranca sin elevar (manifiesto asInvoker) y solo pide permisos en el momento
/// de reparar. El usuario ve un unico dialogo UAC y la ventana principal no se reinicia.
/// </summary>
public sealed class ElevatedLauncher
{
    public const string WorkerSwitch = "--worker";
    public const string PipeSwitch = "--pipe";
    public const string AreaSwitch = "--area";
    public const string AdminSwitch = "--admin";

    private readonly LoggingService _log;

    public ElevatedLauncher(LoggingService log) => _log = log;

    /// <summary>Lanza el worker que realiza la reparacion.</summary>
    public Process? Launch(string areaKey, string pipeName) =>
        LaunchElevated($"reparar el area '{areaKey}'",
            WorkerSwitch, AreaSwitch, areaKey, PipeSwitch, pipeName);

    /// <summary>
    /// Devuelve null si el usuario rechazo el UAC.
    /// Los argumentos van por ArgumentList: .NET los escapa, no se concatena nada.
    /// </summary>
    private Process? LaunchElevated(string purpose, params string[] arguments)
    {
        var executable = Environment.ProcessPath
            ?? throw new RepairException(RepairErrorCode.ErrorInesperado,
                   "No se pudo determinar la ruta del ejecutable.");

        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,  // obligatorio para el verbo runas
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        _log.Info($"Solicitando elevacion para {purpose}.");

        try
        {
            var process = Process.Start(info);
            if (process is null)
                throw new RepairException(RepairErrorCode.ErrorInesperado,
                    "El sistema no devolvio un proceso al elevar.");

            _log.Info($"Proceso elevado iniciado (PID {process.Id}).");
            return process;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            _log.Warn("El usuario cancelo el dialogo de permisos de Windows.");
            return null;
        }
    }
}
