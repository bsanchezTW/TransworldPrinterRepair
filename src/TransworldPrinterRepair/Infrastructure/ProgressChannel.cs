using System.IO;
using System.IO.Pipes;
using System.Text;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Infrastructure;

/// <summary>
/// Canal de progreso entre el worker elevado y la UI.
///
/// Se usa un named pipe y no la salida estandar porque lanzar un proceso elevado exige
/// UseShellExecute = true, y eso impide redirigir stdout. El pipe lo crea el proceso NO
/// elevado; el worker, que corre con integridad mas alta, puede abrirlo sin problema.
/// </summary>
public sealed class ProgressServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;

    public string PipeName { get; }

    public ProgressServer()
    {
        PipeName = "TransworldPrinterRepair." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Espera a que el worker conecte y entrega cada mensaje segun llega.
    /// Si el worker nunca conecta (el usuario rechazo el UAC, o el proceso murio antes),
    /// se sale por el token de cancelacion y la UI se queda con el codigo de salida.
    /// </summary>
    public async Task ReceiveAsync(Action<ProgressMessage> onMessage, CancellationToken cancellationToken)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var reader = new StreamReader(_pipe, new UTF8Encoding(false));

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // El worker termino y cerro el pipe: fin normal de la transmision.
                break;
            }

            if (line is null) break;
            if (line.Length == 0) continue;

            var message = ProgressMessage.FromJson(line);
            if (message is not null) onMessage(message);
        }
    }

    public void Dispose() => _pipe.Dispose();
}

/// <summary>Lado del worker: escribe mensajes de progreso hacia la UI.</summary>
public sealed class ProgressClient : IDisposable
{
    private readonly NamedPipeClientStream? _pipe;
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    /// <summary>
    /// Un fallo al conectar no es motivo para abortar: la reparacion es lo importante y
    /// la UI todavia puede leer el codigo de salida del proceso.
    /// </summary>
    public ProgressClient(string? pipeName, LoggingService log)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) return;

        try
        {
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            _pipe.Connect(10_000);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            log.Warn("No se pudo abrir el canal de progreso: " + LoggingService.Describe(ex));
            _pipe?.Dispose();
            _pipe = null;
            _writer = null;
        }
    }

    public void Send(ProgressMessage message)
    {
        if (_writer is null) return;

        try
        {
            lock (_gate) _writer.WriteLine(message.ToJson());
        }
        catch
        {
            // La UI pudo cerrarse antes de tiempo; el worker debe terminar su trabajo igual.
        }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
    }
}
