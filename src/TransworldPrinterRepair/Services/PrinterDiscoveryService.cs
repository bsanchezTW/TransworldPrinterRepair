using Transworld.PrinterRepair.Infrastructure;

namespace Transworld.PrinterRepair.Services;

/// <summary>Resultado de clasificar las colas del equipo.</summary>
public sealed record PrinterInventory(
    IReadOnlyList<PrinterInfo> ToRemove,
    IReadOnlyList<PrinterInfo> ToKeep);

/// <summary>
/// Enumera las impresoras del equipo y separa las fisicas (que se eliminan para reparar)
/// de las virtuales (Print to PDF, XPS, Fax, OneNote), que se conservan siempre: borrarlas
/// no aporta nada a la reparacion y rompe flujos de trabajo del usuario.
/// </summary>
public sealed class PrinterDiscoveryService
{
    private readonly PrinterApi _printers;
    private readonly LoggingService _log;

    public PrinterDiscoveryService(PrinterApi printers, LoggingService log)
    {
        _printers = printers;
        _log = log;
    }

    public IReadOnlyList<PrinterInfo> ListAll() => _printers.ListPrinters();

    public PrinterInventory Classify(AppConfig config)
    {
        var remove = new List<PrinterInfo>();
        var keep = new List<PrinterInfo>();

        foreach (var printer in ListAll())
        {
            if (IsProtected(printer, config)) keep.Add(printer);
            else remove.Add(printer);
        }

        _log.Info($"Detectadas {remove.Count + keep.Count} impresoras: " +
                  $"{remove.Count} a eliminar, {keep.Count} conservadas.");

        foreach (var p in keep)
            _log.Info($"  conservar: {p.Name} [driver: {p.DriverName}] [puerto: {p.PortName}]");

        foreach (var p in remove)
            _log.Info($"  eliminar : {p.Name} [driver: {p.DriverName}] [puerto: {p.PortName}]");

        return new PrinterInventory(remove, keep);
    }

    /// <summary>Impresora virtual de Windows u Office, que nunca se toca.</summary>
    private static bool IsProtected(PrinterInfo printer, AppConfig config)
    {
        foreach (var driver in config.KeepPrinterDrivers)
        {
            if (printer.DriverName.Contains(driver, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var port in config.KeepPrinterPorts)
        {
            if (printer.PortName.StartsWith(port, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
