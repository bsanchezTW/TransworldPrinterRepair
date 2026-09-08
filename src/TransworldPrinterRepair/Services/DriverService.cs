using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Transworld.PrinterRepair.Infrastructure;

namespace Transworld.PrinterRepair.Services;

public enum DriverPlanKind
{
    /// <summary>El driver del fabricante ya esta publicado en el spooler.</summary>
    AlreadyInstalled,

    /// <summary>Hay que extraer e instalar el paquete embebido.</summary>
    InstallVendor,

    /// <summary>Windows bloquea drivers de terceros; se usa el driver de clase IPP.</summary>
    UseIppFallback,
}

public sealed record DriverPlan(DriverPlanKind Kind, string DriverName, string Reason);

/// <summary>
/// Extrae el paquete de driver embebido en el ejecutable y lo instala en Windows.
///
/// El paquete se guarda en cache bajo ProgramData indexado por hash: la primera reparacion
/// descomprime, las siguientes reutilizan la carpeta y son notablemente mas rapidas.
/// </summary>
public sealed class DriverService
{
    private static readonly TimeSpan PnpUtilTimeout = TimeSpan.FromMinutes(5);

    private readonly PrinterApi _printers;
    private readonly ProcessRunner _runner;
    private readonly LoggingService _log;

    public DriverService(PrinterApi printers, ProcessRunner runner, LoggingService log)
    {
        _printers = printers;
        _runner = runner;
        _log = log;
    }

    // =====================================================================
    // Decision
    // =====================================================================

    /// <summary>
    /// Decide que hacer antes de tocar nada. Separar la decision de la ejecucion permite
    /// que la pantalla de progreso muestre estados reales en cada paso.
    /// </summary>
    public DriverPlan ResolvePlan(DriverPackage package)
    {
        if (SystemChecks.IsProtectedPrintModeEnabled())
        {
            _log.Warn("Windows Protected Print Mode esta activo: el sistema rechaza los drivers " +
                      "de terceros. Se usara " + SystemChecks.IppClassDriver + ".");

            if (!_printers.IsDriverInstalled(SystemChecks.IppClassDriver))
                throw new RepairException(RepairErrorCode.DriverBloqueadoPorWindows,
                    "Protected Print Mode activo y el driver de clase IPP no esta disponible.");

            return new DriverPlan(DriverPlanKind.UseIppFallback, SystemChecks.IppClassDriver,
                "Windows bloquea los controladores de fabricante en este equipo.");
        }

        if (_printers.IsDriverInstalled(package.DriverName))
        {
            _log.Info($"El driver '{package.DriverName}' ya esta publicado en el spooler.");
            return new DriverPlan(DriverPlanKind.AlreadyInstalled, package.DriverName,
                "El controlador ya estaba instalado.");
        }

        return new DriverPlan(DriverPlanKind.InstallVendor, package.DriverName,
            "Se instalara " + package.Model + ".");
    }

    /// <summary>Comprobacion final de que el spooler reconoce el driver.</summary>
    public void VerifyInstalled(string driverName)
    {
        if (_printers.IsDriverInstalled(driverName)) return;

        throw new RepairException(RepairErrorCode.DriverNoInstalado,
            $"El driver '{driverName}' no aparece en el spooler tras instalarlo. " +
            "Comprueba que el nombre configurado coincide exactamente con el del INF.");
    }

    // =====================================================================
    // Extraccion
    // =====================================================================

    /// <summary>Descomprime el paquete si hace falta y devuelve la ruta del INF en disco.</summary>
    public string ExtractPackage(DriverPackage package)
    {
        var hash = ComputeHash(package.Zip);
        var folder = Path.Combine(AppPaths.DriverCacheDirectory,
            Path.GetFileNameWithoutExtension(package.Zip), hash);
        var marker = Path.Combine(folder, ".complete");
        var infPath = Path.Combine(folder, package.Inf.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(marker) && File.Exists(infPath))
        {
            _log.Info($"Paquete ya extraido en cache: {folder}");
            return infPath;
        }

        _log.Info($"Extrayendo {package.Zip} en {folder}");

        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        using (var stream = OpenEmbeddedZip(package.Zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            ExtractSafely(archive, folder);
        }

        if (!File.Exists(infPath))
            throw new RepairException(RepairErrorCode.DriverNoInstalado,
                $"El paquete {package.Zip} no contiene el INF esperado: {package.Inf}");

        File.WriteAllText(marker, DateTime.Now.ToString("O"));
        _log.Info($"Paquete extraido. INF: {infPath}");
        return infPath;
    }

    /// <summary>
    /// Extraccion con proteccion contra "zip slip": una entrada con ../ podria escribir
    /// fuera de la carpeta de destino. Los paquetes son de confianza, pero la comprobacion
    /// es barata y este codigo corre elevado.
    /// </summary>
    private void ExtractSafely(ZipArchive archive, string destination)
    {
        var root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // entrada de carpeta

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"Entrada ignorada por ruta sospechosa: {entry.FullName}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static Stream OpenEmbeddedZip(string zipName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n =>
            n.EndsWith("." + zipName, StringComparison.OrdinalIgnoreCase) ||
            n.Equals(zipName, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
            throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                $"El ejecutable no contiene el paquete de driver {zipName}.");

        return assembly.GetManifestResourceStream(resource)!;
    }

    private static string ComputeHash(string zipName)
    {
        using var stream = OpenEmbeddedZip(zipName);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream))[..16];
    }

    // =====================================================================
    // Instalacion
    // =====================================================================

    /// <summary>
    /// Almacena el paquete en el Driver Store. Se usa pnputil porque resuelve tambien los INF
    /// auxiliares del paquete (enumeradores de bus, catalogos) que la API por si sola no cubre.
    /// Un fallo aqui no aborta: la via por API todavia puede funcionar.
    /// </summary>
    public void StageInDriverStore(string infPath)
    {
        try
        {
            var result = _runner.Run(
                ProcessRunner.SystemTool("pnputil.exe"),
                new[] { "/add-driver", infPath, "/install" },
                PnpUtilTimeout);

            // 0 = correcto. 3010 = correcto pero pide reinicio, que no hace falta para imprimir.
            if (result.ExitCode is 0 or 3010)
                _log.Info("pnputil almaceno el paquete en el Driver Store.");
            else
                _log.Warn($"pnputil devolvio {result.ExitCode}; se continua por la API del spooler.");
        }
        catch (Exception ex)
        {
            _log.Warn("pnputil fallo: " + LoggingService.Describe(ex));
        }
    }

    /// <summary>Publica el driver en el spooler para que AddPrinter pueda usarlo.</summary>
    public void PublishToSpooler(string infPath, string driverName)
    {
        try
        {
            var storeInf = _printers.UploadDriverPackage(infPath);
            _printers.PublishDriver(storeInf, driverName);
        }
        catch (RepairException ex)
        {
            // Segundo intento con el INF original: si pnputil ya almaceno el paquete,
            // publicar desde la ruta extraida suele funcionar igualmente.
            _log.Warn("Primer intento de publicacion fallido: " + ex.Message);
            _log.Warn("Reintentando la publicacion del driver desde el INF original.");
            _printers.PublishDriver(infPath, driverName);
        }
    }
}
