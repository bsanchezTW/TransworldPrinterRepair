using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace Transworld.PrinterRepair.Services;

/// <summary>Paquete de driver embebido en el ejecutable.</summary>
public sealed class DriverPackage
{
    /// <summary>Nombre del ZIP embebido, p. ej. "HP_LaserJet_MFP_M426fdw.zip".</summary>
    public string Zip { get; init; } = string.Empty;

    /// <summary>Ruta del INF concreto DENTRO del zip. Nunca se usa un comodin *.inf:
    /// los paquetes traen tambien INF de escaner y stubs USB que no aportan nada.</summary>
    public string Inf { get; init; } = string.Empty;

    /// <summary>Nombre exacto con el que el driver queda publicado en el spooler.
    /// Leido del INF; debe coincidir caracter a caracter o AddPrinter falla.</summary>
    public string DriverName { get; init; } = string.Empty;

    /// <summary>Modelo comercial, solo para mostrar y registrar en el log.</summary>
    public string Model { get; init; } = string.Empty;
}

/// <summary>Un area de Transworld y la impresora que le corresponde.</summary>
public sealed class AreaDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string DriverPackage { get; init; } = string.Empty;

    /// <summary>Nombre generado automaticamente. El administrador nunca lo escribe a mano.</summary>
    [JsonIgnore]
    public string PrinterName => PrinterNaming.ForArea(Name);
}

public sealed class AppConfig
{
    public int SchemaVersion { get; init; } = 1;
    public Dictionary<string, DriverPackage> DriverPackages { get; init; } = new();
    public List<AreaDefinition> Areas { get; init; } = new();
    public List<string> KeepPrinterDrivers { get; init; } = new();
    public List<string> KeepPrinterPorts { get; init; } = new();

    public AreaDefinition? FindArea(string key) =>
        Areas.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    public DriverPackage? FindPackage(AreaDefinition area) =>
        DriverPackages.TryGetValue(area.DriverPackage, out var p) ? p : null;
}

/// <summary>Genera el nombre de impresora a partir del area: "IMPRESORA [AREA]".</summary>
public static class PrinterNaming
{
    public const string Prefix = "IMPRESORA ";

    public static string ForArea(string areaName) => Prefix + Normalize(areaName);

    /// <summary>
    /// Mayusculas sin tildes y sin caracteres que Windows prohibe en un nombre de impresora
    /// (\ , !). "Facturacion" -> FACTURACION, "Bodega Chica" -> BODEGA CHICA.
    /// </summary>
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch) || ch == '-')
                sb.Append(char.ToUpperInvariant(ch));
            else if (char.IsWhiteSpace(ch))
                sb.Append(' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Validaciones de entrada. Nada procedente de configuracion llega a una API sin pasar por aqui.</summary>
public static class Validation
{
    /// <summary>Acepta solo IPv4 unicast enrutable a una impresora de LAN.</summary>
    public static bool IsUsablePrinterIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!IPAddress.TryParse(value.Trim(), out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var b = ip.GetAddressBytes();
        if (b[0] == 0) return false;                     // 0.0.0.0/8
        if (b[0] == 127) return false;                   // loopback
        if (b[0] >= 224) return false;                   // multicast y reservadas
        if (b[0] == 169 && b[1] == 254) return false;    // APIPA
        if (b[3] == 255) return false;                   // broadcast de subred /24

        // Rechaza formas no canonicas ("192.168.190.08", "192.168.190.8 ") que luego
        // no coincidirian al comparar contra la configuracion de un puerto existente.
        return ip.ToString() == value.Trim();
    }

    /// <summary>Un nombre de impresora valido para el spooler de Windows.</summary>
    public static bool IsValidPrinterName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 220
        && value.IndexOfAny(new[] { '\\', ',', '!', '"' }) < 0
        && !value.Any(char.IsControl);
}
