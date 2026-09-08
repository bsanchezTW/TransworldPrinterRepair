using System.Diagnostics;
using System.IO;
using System.Text;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Infrastructure;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Ejecuta herramientas del sistema. Los argumentos se pasan SIEMPRE mediante
/// ArgumentList, nunca concatenando cadenas: asi una ruta con espacios, comillas o
/// caracteres especiales no puede convertirse en inyeccion de comandos.
/// </summary>
public sealed class ProcessRunner
{
    private readonly LoggingService _log;

    public ProcessRunner(LoggingService log) => _log = log;

    public ProcessResult Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var args = arguments.ToList();

        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in args) info.ArgumentList.Add(argument);

        _log.Info($"Ejecutando: {fileName} {string.Join(' ', args.Select(Quote))}");

        using var process = Process.Start(info)
            ?? throw new RepairException(RepairErrorCode.ErrorInesperado,
                   "No se pudo iniciar " + fileName);

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new RepairException(RepairErrorCode.ErrorInesperado,
                $"{Path.GetFileName(fileName)} no termino en {timeout.TotalSeconds:0} segundos.");
        }

        // Asegura que los lectores asincronos han vaciado sus buffers.
        process.WaitForExit();

        var text = output.ToString().Trim();
        _log.Info($"{Path.GetFileName(fileName)} termino con codigo {process.ExitCode}.");
        if (text.Length > 0) _log.Info("Salida: " + text.Replace(Environment.NewLine, " | "));

        return new ProcessResult(process.ExitCode, text);
    }

    /// <summary>Solo para el log: refleja como se pasaron los argumentos.</summary>
    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;

    /// <summary>Ruta absoluta a una herramienta de System32, sin depender del PATH del usuario.</summary>
    public static string SystemTool(string exeName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);
}
