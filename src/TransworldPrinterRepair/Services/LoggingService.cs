using System.IO;
using System.Text;

namespace Transworld.PrinterRepair.Services;

/// <summary>
/// Log diario en ProgramData. Lo comparten el proceso UI y el worker elevado, por eso se abre
/// con FileShare.ReadWrite. Escribir en el log nunca puede tumbar una reparacion: todo fallo
/// de E/S se traga en silencio.
/// </summary>
public sealed class LoggingService
{
    private static readonly object Gate = new();
    private readonly string _source;
    private readonly bool _enabled;

    /// <summary>Se invoca ademas del archivo, para reflejar la linea en la UI si procede.</summary>
    public event Action<string>? LineWritten;

    public LoggingService(string source)
    {
        _source = source;
        _enabled = AppPaths.TryEnsureDirectories();
    }

    public string CurrentFile =>
        Path.Combine(AppPaths.LogsDirectory, $"repair-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) =>
        Write("ERROR", message + " :: " + Describe(ex));

    /// <summary>Separador visible entre ejecuciones, para leer el archivo de un vistazo.</summary>
    public void Banner(string title)
    {
        Write("INFO", new string('=', 60));
        Write("INFO", title);
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level,-5}] [{_source}] {message}";

        try { LineWritten?.Invoke(line); } catch { /* la UI no debe romper el log */ }

        if (!_enabled) return;

        try
        {
            lock (Gate)
            {
                using var stream = new FileStream(
                    CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Sin permisos de escritura el programa sigue funcionando, solo pierde el registro.
        }
    }

    /// <summary>Aplana la cadena de excepciones incluyendo HRESULT, que es lo util para diagnosticar.</summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var current = (Exception?)ex;

        while (current is not null)
        {
            sb.Append(current.GetType().Name)
              .Append(": ")
              .Append(current.Message)
              .Append(" (HRESULT 0x")
              .Append(current.HResult.ToString("X8"))
              .Append(')');

            if (current is System.ComponentModel.Win32Exception w32)
            {
                sb.Append(" [Win32 ").Append(w32.NativeErrorCode).Append(']');
            }

            current = current.InnerException;
            if (current is not null) sb.Append(" <- ");
        }

        return sb.ToString();
    }
}
