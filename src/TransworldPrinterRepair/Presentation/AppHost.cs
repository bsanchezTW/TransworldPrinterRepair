using Transworld.PrinterRepair.Infrastructure;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation;

/// <summary>
/// Contenedor sencillo de servicios para el proceso de interfaz. No se usa un contenedor de
/// inyeccion de dependencias porque el grafo es pequenno, fijo y de un solo hilo, y anadir uno
/// solo sumaria peso al ejecutable portable.
/// </summary>
public sealed class AppHost
{
    public AppHost()
    {
        Log = new LoggingService("ui");
        Configuration = new ConfigurationService(Log);
        Printers = new PrinterApi(Log);
        Ports = new PortApi(Log);
        Network = new NetworkService(Log);
        Launcher = new ElevatedLauncher(Log);
        Coordinator = new RepairCoordinator(Launcher, Printers, Log);
    }

    public LoggingService Log { get; }
    public ConfigurationService Configuration { get; }
    public PrinterApi Printers { get; }
    public PortApi Ports { get; }
    public NetworkService Network { get; }
    public ElevatedLauncher Launcher { get; }
    public RepairCoordinator Coordinator { get; }

    public AppConfig Config => Configuration.Load();
}
