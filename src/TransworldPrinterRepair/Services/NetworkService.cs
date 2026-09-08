using System.Net.Sockets;

namespace Transworld.PrinterRepair.Services;

/// <summary>Comprobacion de que la impresora responde realmente en la red.</summary>
public sealed class NetworkService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly LoggingService _log;

    public NetworkService(LoggingService log) => _log = log;

    /// <summary>
    /// Intenta abrir una conexion TCP al puerto de impresion. Es mucho mas fiable que un ping:
    /// muchas impresoras y firewalls corporativos descartan ICMP pero aceptan el 9100.
    /// </summary>
    public bool CanReach(string ip, int port, TimeSpan? timeout = null)
    {
        if (!Validation.IsUsablePrinterIp(ip))
        {
            _log.Warn($"No se comprueba la conexion: {ip} no es una IP valida.");
            return false;
        }

        var limit = timeout ?? DefaultTimeout;

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(ip, port);

            if (!connect.Wait(limit))
            {
                _log.Warn($"Sin respuesta de {ip}:{port} tras {limit.TotalSeconds:0} segundos.");
                return false;
            }

            if (connect.IsFaulted)
            {
                _log.Warn($"Error conectando a {ip}:{port}: {connect.Exception?.GetBaseException().Message}");
                return false;
            }

            var connected = client.Connected;
            _log.Info($"Conexion TCP a {ip}:{port}: {(connected ? "correcta" : "rechazada")}.");
            return connected;
        }
        catch (Exception ex)
        {
            _log.Warn($"Error comprobando {ip}:{port}: " + LoggingService.Describe(ex));
            return false;
        }
    }
}
