using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Transworld.PrinterRepair.Services;
using static Transworld.PrinterRepair.Infrastructure.Interop.NativeMethods;

namespace Transworld.PrinterRepair.Infrastructure;

/// <summary>
/// Control del servicio Print Spooler mediante advapi32, sin depender de PowerShell
/// ni de System.ServiceProcess, para que el ejecutable siga siendo autocontenido.
/// </summary>
public sealed class SpoolerControl
{
    private const string ServiceName = "Spooler";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly LoggingService _log;

    public SpoolerControl(LoggingService log) => _log = log;

    public bool IsRunning() => QueryState() == SERVICE_RUNNING;

    /// <summary>Arranca el spooler si esta parado y lo deja en inicio automatico.</summary>
    public void EnsureRunning()
    {
        var state = QueryState();
        _log.Info($"Estado del spooler: {Describe(state)}");

        if (state == SERVICE_RUNNING)
        {
            TryEnsureAutomaticStart();
            return;
        }

        Start();
        TryEnsureAutomaticStart();
    }

    /// <summary>
    /// Reinicia el spooler limpiando los trabajos atascados. Se usa solo cuando quedan
    /// trabajos que la purga por impresora no consiguio eliminar.
    /// </summary>
    public void RestartClearingQueue()
    {
        _log.Info("Reiniciando el spooler y limpiando la cola en disco.");

        try
        {
            Stop();
            ClearSpoolFolder();
        }
        catch (Exception ex)
        {
            _log.Warn("No se pudo detener el spooler limpiamente: " + LoggingService.Describe(ex));
        }

        Start();
    }

    // =====================================================================

    private uint QueryState()
    {
        using var service = Open(SERVICE_QUERY_STATUS);
        if (!QueryServiceStatus(service.Handle, out var status))
            throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
                "QueryServiceStatus fallo.", PrinterApi.LastError("QueryServiceStatus"));

        return status.dwCurrentState;
    }

    private void Start()
    {
        using var service = Open(SERVICE_START | SERVICE_QUERY_STATUS);

        if (!StartService(service.Handle, 0, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            // 1056 = el servicio ya se esta iniciando; no es un fallo.
            if (code != 1056)
                throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
                    $"No se pudo iniciar el spooler (Win32 {code}).", new Win32Exception(code));
        }

        WaitFor(service.Handle, SERVICE_RUNNING);
        _log.Info("Spooler en ejecucion.");
    }

    private void Stop()
    {
        using var service = Open(SERVICE_STOP | SERVICE_QUERY_STATUS);

        if (!ControlService(service.Handle, SERVICE_CONTROL_STOP, out _))
        {
            var code = Marshal.GetLastWin32Error();
            // 1062 = el servicio no estaba iniciado.
            if (code != 1062)
                throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
                    $"No se pudo detener el spooler (Win32 {code}).", new Win32Exception(code));
        }

        WaitFor(service.Handle, SERVICE_STOPPED);
    }

    private void WaitFor(IntPtr handle, uint desired)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!QueryServiceStatus(handle, out var status)) break;
            if (status.dwCurrentState == desired) return;
            Thread.Sleep(250);
        }

        throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
            $"El spooler no alcanzo el estado {Describe(desired)} en {Timeout.TotalSeconds:0} segundos.");
    }

    /// <summary>
    /// Vacia %SystemRoot%\System32\spool\PRINTERS. Solo se puede hacer con el servicio parado.
    /// No se toca nada mas del sistema de archivos ni del registro.
    /// </summary>
    private void ClearSpoolFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "PRINTERS");

        if (!Directory.Exists(folder)) return;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                _log.Warn($"No se pudo borrar el trabajo en cola {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        _log.Info($"Trabajos en cola eliminados del disco: {removed}");
    }

    private void TryEnsureAutomaticStart()
    {
        try
        {
            using var service = Open(SERVICE_CHANGE_CONFIG);
            if (ChangeServiceConfig(service.Handle, SERVICE_NO_CHANGE, SERVICE_AUTO_START,
                    SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
            {
                _log.Info("Spooler configurado en inicio automatico.");
            }
        }
        catch (Exception ex)
        {
            // Si el equipo no permite cambiar la configuracion del servicio no es critico.
            _log.Warn("No se pudo fijar el inicio automatico del spooler: " + LoggingService.Describe(ex));
        }
    }

    private static string Describe(uint state) => state switch
    {
        SERVICE_STOPPED => "detenido",
        SERVICE_START_PENDING => "iniciandose",
        SERVICE_STOP_PENDING => "deteniendose",
        SERVICE_RUNNING => "en ejecucion",
        _ => "desconocido (" + state + ")",
    };

    private static ServiceHandle Open(uint access)
    {
        var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
            throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
                "No se pudo abrir el gestor de servicios.", PrinterApi.LastError("OpenSCManager"));

        var service = OpenService(manager, ServiceName, access);
        if (service == IntPtr.Zero)
        {
            var error = PrinterApi.LastError("OpenService");
            CloseServiceHandle(manager);
            throw new RepairException(RepairErrorCode.SpoolerNoDisponible,
                "No se pudo abrir el servicio Spooler.", error);
        }

        return new ServiceHandle(manager, service);
    }

    private sealed class ServiceHandle : IDisposable
    {
        private readonly IntPtr _manager;
        public IntPtr Handle { get; }

        public ServiceHandle(IntPtr manager, IntPtr service)
        {
            _manager = manager;
            Handle = service;
        }

        public void Dispose()
        {
            CloseServiceHandle(Handle);
            CloseServiceHandle(_manager);
        }
    }
}
