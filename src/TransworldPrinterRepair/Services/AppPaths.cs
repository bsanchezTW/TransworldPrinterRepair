using System.IO;

namespace Transworld.PrinterRepair.Services;

/// <summary>
/// Rutas de trabajo. El ejecutable es portable y NUNCA escribe junto a si mismo:
/// puede estar en Descargas, en un USB o en una ruta de red sin permiso de escritura.
/// Todo el estado mutable vive en ProgramData, legible por cualquier usuario del equipo.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Transworld", "PrinterRepair");

    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string DriverCacheDirectory => Path.Combine(Root, "cache", "drivers");
    public static string OverridesFile => Path.Combine(Root, "overrides.json");
    public static string StateFile => Path.Combine(Root, "state.json");

    /// <summary>
    /// Crea el arbol de carpetas. Devuelve false en vez de lanzar: un fallo aqui no debe
    /// impedir la reparacion, solo degrada el registro de logs.
    /// </summary>
    public static bool TryEnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(DriverCacheDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
