using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transworld.PrinterRepair.Services;

/// <summary>
/// Carga la configuracion de fabrica embebida en el ejecutable y le superpone lo que el
/// administrador haya cambiado en ProgramData. Asi el .exe funciona recien copiado en
/// cualquier PC, y las ediciones locales sobreviven a una sustitucion del binario.
/// </summary>
public sealed class ConfigurationService
{
    private const string AreasResource = "areas.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LoggingService _log;
    private AppConfig? _cached;

    public ConfigurationService(LoggingService log) => _log = log;

    public AppConfig Load()
    {
        if (_cached is not null) return _cached;

        var config = LoadEmbedded();
        ApplyOverrides(config);
        _cached = config;
        return config;
    }

    public void Invalidate() => _cached = null;

    private AppConfig LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(AreasResource)
            ?? throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                   "No se encontro el recurso embebido " + AreasResource + ".");

        var config = JsonSerializer.Deserialize<AppConfig>(stream, Json)
            ?? throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                   "areas.json embebido esta vacio o mal formado.");

        // Una IP invalida en la configuracion de fabrica es un error de despliegue,
        // no algo que deba descubrirse a mitad de una reparacion.
        foreach (var area in config.Areas)
        {
            if (!Validation.IsUsablePrinterIp(area.Ip))
                _log.Warn($"Area {area.Name} tiene una IP no valida en la configuracion: {area.Ip}");

            if (!config.DriverPackages.ContainsKey(area.DriverPackage))
                _log.Warn($"Area {area.Name} referencia un paquete inexistente: {area.DriverPackage}");
        }

        return config;
    }

    // ---- Overrides del administrador (solo IPs) ----

    private sealed class Overrides
    {
        public Dictionary<string, string> AreaIps { get; set; } = new();
    }

    private void ApplyOverrides(AppConfig config)
    {
        var file = AppPaths.OverridesFile;
        if (!File.Exists(file)) return;

        try
        {
            var overrides = JsonSerializer.Deserialize<Overrides>(File.ReadAllText(file), Json);
            if (overrides is null) return;

            foreach (var entry in overrides.AreaIps)
            {
                var area = config.FindArea(entry.Key);
                if (area is null) continue;

                if (!Validation.IsUsablePrinterIp(entry.Value))
                {
                    _log.Warn($"Se ignora la IP personalizada de {entry.Key}: {entry.Value} no es valida.");
                    continue;
                }

                _log.Info($"IP personalizada para {area.Name}: {area.Ip} -> {entry.Value}");
                area.Ip = entry.Value;
            }
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo leer overrides.json; se usa la configuracion de fabrica.", ex);
        }
    }

    /// <summary>Guarda una IP personalizada. Requiere permiso de escritura en ProgramData.</summary>
    public void SaveAreaIpOverride(string areaKey, string ip)
    {
        if (!Validation.IsUsablePrinterIp(ip))
            throw new RepairException(RepairErrorCode.ConfiguracionInvalida, "IP no valida: " + ip);

        AppPaths.TryEnsureDirectories();
        var file = AppPaths.OverridesFile;

        Overrides overrides;
        try
        {
            overrides = File.Exists(file)
                ? JsonSerializer.Deserialize<Overrides>(File.ReadAllText(file), Json) ?? new Overrides()
                : new Overrides();
        }
        catch
        {
            overrides = new Overrides();
        }

        overrides.AreaIps[areaKey] = ip;
        File.WriteAllText(file, JsonSerializer.Serialize(overrides, Json));
        _log.Info($"Guardada IP personalizada para {areaKey}: {ip}");
        Invalidate();
    }

    /// <summary>Devuelve un area a la IP de fabrica descartando la personalizacion.</summary>
    public void RemoveAreaIpOverride(string areaKey)
    {
        var file = AppPaths.OverridesFile;
        if (!File.Exists(file)) return;

        try
        {
            var overrides = JsonSerializer.Deserialize<Overrides>(File.ReadAllText(file), Json);
            if (overrides is null || !overrides.AreaIps.Remove(areaKey)) return;

            File.WriteAllText(file, JsonSerializer.Serialize(overrides, Json));
            _log.Info($"Eliminada la IP personalizada de {areaKey}; vuelve al valor de fabrica.");
            Invalidate();
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo eliminar la IP personalizada.", ex);
            throw new RepairException(RepairErrorCode.ConfiguracionInvalida,
                "No se pudo escribir overrides.json.", ex);
        }
    }

    /// <summary>IP de fabrica de un area, ignorando lo que el administrador haya cambiado.</summary>
    public string? GetFactoryIp(string areaKey) =>
        LoadEmbedded().FindArea(areaKey)?.Ip;

    // ---- Ultima area utilizada en este equipo ----

    private sealed class LocalState
    {
        public string? LastAreaKey { get; set; }
    }

    public string? GetLastAreaKey()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFile)) return null;
            return JsonSerializer.Deserialize<LocalState>(File.ReadAllText(AppPaths.StateFile), Json)?.LastAreaKey;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Preferencia de conveniencia: si no se puede guardar, no pasa nada.</summary>
    public void SetLastAreaKey(string areaKey)
    {
        try
        {
            AppPaths.TryEnsureDirectories();
            File.WriteAllText(AppPaths.StateFile,
                JsonSerializer.Serialize(new LocalState { LastAreaKey = areaKey }, Json));
        }
        catch
        {
        }
    }
}
