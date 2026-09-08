using System.Security.Principal;
using Microsoft.Win32;

namespace Transworld.PrinterRepair.Infrastructure;

/// <summary>Comprobaciones del entorno Windows previas a la reparacion.</summary>
public static class SystemChecks
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Windows Protected Print Mode (Windows 11 24H2 y posteriores, opcional en Windows 10 22H2)
    /// bloquea la instalacion de drivers de impresora de terceros: solo admite el
    /// Microsoft IPP Class Driver. Sin esta comprobacion la reparacion fallaria mas adelante
    /// con un error opaco, en vez de poder degradar a la alternativa que si funciona.
    /// </summary>
    public static bool IsProtectedPrintModeEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\WindowsProtectedPrintMode");

            if (key is null) return false;
            return Convert.ToInt32(key.GetValue("WindowsProtectedPrintModeEnabled") ?? 0) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Driver de clase que Windows incluye de serie y que funciona incluso con
    /// Protected Print Mode activo. Es el plan B cuando no se puede instalar el del fabricante.</summary>
    public const string IppClassDriver = "Microsoft IPP Class Driver";

    public static string DescribeWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string;
            var build = key?.GetValue("CurrentBuildNumber") as string;

            // Windows 11 se sigue identificando como "Windows 10" en ProductName;
            // la build es lo que realmente distingue las dos versiones.
            if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
                product = product.Replace("Windows 10", "Windows 11");

            return $"{product} {display} (build {build}) {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }
}
