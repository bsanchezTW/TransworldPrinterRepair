using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using Transworld.PrinterRepair.Infrastructure;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>Fila editable del panel administrativo.</summary>
public sealed class AdminAreaViewModel : ObservableObject
{
    private readonly AppHost _host;
    private string _ip;
    private string? _status;

    public AdminAreaViewModel(AppHost host, AreaDefinition area, DriverPackage? package)
    {
        _host = host;
        Area = area;
        _ip = area.Ip;
        Model = package?.Model ?? "(paquete no encontrado)";
        DriverName = package?.DriverName ?? "-";

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        TestCommand = new RelayCommand(Test);
    }

    public AreaDefinition Area { get; }
    public string Name => Area.Name;
    public string PrinterName => Area.PrinterName;
    public string Model { get; }
    public string DriverName { get; }

    public string Ip
    {
        get => _ip;
        set => SetProperty(ref _ip, value);
    }

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand TestCommand { get; }

    private void Save()
    {
        var value = (Ip ?? string.Empty).Trim();

        if (!Validation.IsUsablePrinterIp(value))
        {
            Status = "La dirección IP no es válida.";
            return;
        }

        try
        {
            _host.Configuration.SaveAreaIpOverride(Area.Key, value);
            Area.Ip = value;
            Status = "Guardado.";
        }
        catch (Exception ex)
        {
            Status = "No se pudo guardar el cambio.";
            _host.Log.Error($"Fallo al guardar la IP de {Area.Key}.", ex);
        }
    }

    private void Reset()
    {
        try
        {
            _host.Configuration.RemoveAreaIpOverride(Area.Key);

            var factory = _host.Configuration.GetFactoryIp(Area.Key);
            if (factory is not null)
            {
                Ip = factory;
                Area.Ip = factory;
            }

            Status = "Restaurada la IP original.";
        }
        catch (Exception ex)
        {
            Status = "No se pudo restaurar la IP.";
            _host.Log.Error($"Fallo al restaurar la IP de {Area.Key}.", ex);
        }
    }

    private void Test()
    {
        var value = (Ip ?? string.Empty).Trim();

        if (!Validation.IsUsablePrinterIp(value))
        {
            Status = "La dirección IP no es válida.";
            return;
        }

        Status = "Probando...";
        var reachable = _host.Network.CanReach(value, PortApi.RawPort);
        Status = reachable
            ? $"Responde en {value}:{PortApi.RawPort}."
            : $"Sin respuesta en {value}:{PortApi.RawPort}.";
    }
}

/// <summary>
/// Panel administrativo. Se ejecuta SIEMPRE en una instancia elevada aparte: el UAC de
/// Windows es la autenticacion, no hay ninguna contrasena dentro del programa.
///
/// Los drivers y las areas van compilados en el ejecutable, asi que aqui no se pueden
/// anadir ni sustituir: eso exige regenerar el .exe. Lo que si se puede corregir sobre la
/// marcha es la IP de cada area, que es lo que cambia en el dia a dia.
/// </summary>
public sealed class AdminSettingsViewModel : ObservableObject
{
    private readonly AppHost _host;

    public AdminSettingsViewModel(AppHost host, Action closeAction)
    {
        _host = host;

        Areas = new ObservableCollection<AdminAreaViewModel>(
            host.Config.Areas.Select(a => new AdminAreaViewModel(host, a, host.Config.FindPackage(a))));

        OpenLogsCommand = new RelayCommand(OpenLogs);
        CloseCommand = new RelayCommand(closeAction);

        host.Log.Info("Panel administrativo abierto (proceso elevado).");
    }

    public ObservableCollection<AdminAreaViewModel> Areas { get; }

    public ICommand OpenLogsCommand { get; }
    public ICommand CloseCommand { get; }

    public string Title => "Configuración administrativa";

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public string SystemInfo => SystemChecks.DescribeWindows();

    public string ProtectedPrintMode => SystemChecks.IsProtectedPrintModeEnabled()
        ? "Activo — Windows bloquea los controladores de fabricante en este equipo"
        : "Inactivo";

    public string DataFolder => AppPaths.Root;

    public string ExecutablePath => Environment.ProcessPath ?? "(desconocido)";

    public string PackagesInfo =>
        $"{_host.Config.DriverPackages.Count} paquetes de controlador embebidos en el ejecutable";

    private void OpenLogs()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDirectory)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _host.Log.Warn("No se pudo abrir la carpeta de logs: " + LoggingService.Describe(ex));
        }
    }
}
