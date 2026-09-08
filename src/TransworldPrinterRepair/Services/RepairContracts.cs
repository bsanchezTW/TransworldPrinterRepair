using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transworld.PrinterRepair.Services;

/// <summary>Los pasos tecnicos reales de la reparacion, en orden de ejecucion.</summary>
public enum RepairStep
{
    Preparar,
    Detectar,
    EliminarImpresoras,
    LimpiarPuertos,
    ExtraerDriver,
    InstalarDriver,
    PublicarDriver,
    ConfigurarPuerto,
    CrearImpresora,
    VerificarConexion,
    VerificarConfiguracion,
}

public enum StepState
{
    Pending,
    Running,
    Ok,
    Warning,
    Failed,
}

/// <summary>
/// Motivo de fallo en terminos del dominio. La UI traduce esto a lenguaje llano;
/// el HRESULT o el exit code real solo aparecen en el log.
/// </summary>
public enum RepairErrorCode
{
    None = 0,
    UacCancelado,
    PermisosInsuficientes,
    SpoolerNoDisponible,
    ConfiguracionInvalida,
    DriverBloqueadoPorWindows,
    DriverNoInstalado,
    PuertoNoConfigurado,
    ImpresoraNoCreada,
    ImpresoraNoResponde,
    VerificacionFallida,
    ErrorInesperado,
}

/// <summary>Los 5 grupos que ve el usuario en la pantalla de progreso.</summary>
public enum DisplayStage
{
    PreparandoSistema,
    EliminandoImpresoras,
    InstalandoControlador,
    ConfigurandoImpresora,
    VerificandoConexion,
}

public static class StepMapping
{
    /// <summary>Cada paso tecnico alimenta uno de los 5 grupos visibles.</summary>
    public static DisplayStage ToStage(this RepairStep step) => step switch
    {
        RepairStep.Preparar or RepairStep.Detectar
            => DisplayStage.PreparandoSistema,
        RepairStep.EliminarImpresoras or RepairStep.LimpiarPuertos
            => DisplayStage.EliminandoImpresoras,
        RepairStep.ExtraerDriver or RepairStep.InstalarDriver or RepairStep.PublicarDriver
            => DisplayStage.InstalandoControlador,
        RepairStep.ConfigurarPuerto or RepairStep.CrearImpresora
            => DisplayStage.ConfigurandoImpresora,
        _ => DisplayStage.VerificandoConexion,
    };

    public static string Title(this DisplayStage stage) => stage switch
    {
        DisplayStage.PreparandoSistema     => "Preparando sistema",
        DisplayStage.EliminandoImpresoras  => "Eliminando impresoras anteriores",
        DisplayStage.InstalandoControlador => "Instalando controlador",
        DisplayStage.ConfigurandoImpresora => "Configurando impresora",
        _                                  => "Verificando conexion",
    };
}

public static class ErrorMessages
{
    /// <summary>Mensaje comprensible para un trabajador sin conocimientos tecnicos.</summary>
    public static string Friendly(this RepairErrorCode code, string? ip = null, string? printer = null) => code switch
    {
        RepairErrorCode.UacCancelado =>
            "No se concedieron los permisos necesarios. Vuelve a intentarlo y acepta la ventana de Windows que pide autorizacion.",
        RepairErrorCode.PermisosInsuficientes =>
            "Este equipo no permitio realizar los cambios. Avisa al area de Informatica.",
        RepairErrorCode.SpoolerNoDisponible =>
            "El servicio de impresion de Windows no responde. Reinicia el equipo e intentalo de nuevo.",
        RepairErrorCode.ConfiguracionInvalida =>
            "La configuracion de tu area no es valida. Avisa al area de Informatica.",
        RepairErrorCode.DriverBloqueadoPorWindows =>
            "Windows tiene activada una proteccion que impide instalar el controlador de la impresora. Avisa al area de Informatica.",
        RepairErrorCode.DriverNoInstalado =>
            "No fue posible instalar el controlador de la impresora.",
        RepairErrorCode.PuertoNoConfigurado =>
            $"No fue posible configurar la conexion con la impresora{Where(ip)}.",
        RepairErrorCode.ImpresoraNoCreada =>
            "No fue posible crear la impresora en este equipo.",
        RepairErrorCode.ImpresoraNoResponde =>
            $"No fue posible comunicarse con la impresora{Where(ip)}. Comprueba que este encendida y conectada a la red.",
        RepairErrorCode.VerificacionFallida =>
            $"La impresora {printer ?? "de tu area"} se configuro, pero no quedo en el estado esperado.",
        _ =>
            "Ocurrio un problema inesperado durante la reparacion.",
    };

    private static string Where(string? ip) => string.IsNullOrWhiteSpace(ip) ? "" : $" en {ip}";
}

/// <summary>Mensaje que viaja por el named pipe entre el worker elevado y la UI.</summary>
public sealed class ProgressMessage
{
    /// <summary>"step", "log" o "result".</summary>
    public string Kind { get; set; } = "log";

    public RepairStep? Step { get; set; }
    public StepState? State { get; set; }

    /// <summary>Texto corto para la linea de detalle bajo el paso en curso.</summary>
    public string? Detail { get; set; }

    /// <summary>Linea completa para el archivo de log.</summary>
    public string? Message { get; set; }

    public bool Success { get; set; }
    public RepairErrorCode ErrorCode { get; set; } = RepairErrorCode.None;

    /// <summary>Detalle tecnico (HRESULT, exit code). Va al log, nunca a la pantalla.</summary>
    public string? ErrorDetail { get; set; }

    public string? PrinterName { get; set; }
    public string? PortName { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ProgressMessage? FromJson(string line)
    {
        try { return JsonSerializer.Deserialize<ProgressMessage>(line, Options); }
        catch { return null; }
    }

    public static ProgressMessage ForStep(RepairStep step, StepState state, string? detail = null) =>
        new() { Kind = "step", Step = step, State = state, Detail = detail };

    public static ProgressMessage ForLog(string message) =>
        new() { Kind = "log", Message = message };
}

/// <summary>Resultado final de una reparacion.</summary>
public sealed class RepairResult
{
    public bool Success { get; init; }
    public RepairErrorCode ErrorCode { get; init; } = RepairErrorCode.None;
    public string? ErrorDetail { get; init; }
    public string? PrinterName { get; init; }
    public string? PortName { get; init; }

    public static RepairResult Ok(string printerName, string portName) =>
        new() { Success = true, PrinterName = printerName, PortName = portName };

    public static RepairResult Fail(RepairErrorCode code, string? detail = null, string? printerName = null) =>
        new() { Success = false, ErrorCode = code, ErrorDetail = detail, PrinterName = printerName };
}

/// <summary>Excepcion interna que transporta un codigo de dominio hasta el orquestador.</summary>
public sealed class RepairException : Exception
{
    public RepairErrorCode Code { get; }

    public RepairException(RepairErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}
