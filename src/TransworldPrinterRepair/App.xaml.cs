using System.Windows;
using Transworld.PrinterRepair.Infrastructure;
using Transworld.PrinterRepair.Presentation;
using Transworld.PrinterRepair.Presentation.ViewModels;
using Transworld.PrinterRepair.Services;
using Transworld.PrinterRepair.Worker;

namespace Transworld.PrinterRepair;

/// <summary>
/// Un unico ejecutable con tres modos, segun los argumentos:
///
///   (sin argumentos)  interfaz del trabajador, SIN elevar
///   --worker          proceso elevado que repara y no muestra ventana
///   --admin           panel administrativo, elevado
///
/// Que sea el mismo binario es lo que permite entregar un solo archivo portable.
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Has(e.Args, ElevatedLauncher.WorkerSwitch))
        {
            RunWorker(e.Args);
            return;
        }

        if (Has(e.Args, ElevatedLauncher.AdminSwitch))
        {
            RunAdmin();
            return;
        }

        // Diagnostico sin interfaz. Deja un informe en la carpeta de registros.
        // Con --instalar-drivers instala ademas todos los controladores embebidos,
        // sin crear ni eliminar impresoras.
        if (Has(e.Args, "--selftest"))
        {
            Shutdown(SelfTest.Run(installDrivers: Has(e.Args, "--instalar-drivers")));
            return;
        }

        RunUserInterface();
    }

    private void RunWorker(string[] args)
    {
        var area = ValueOf(args, ElevatedLauncher.AreaSwitch);
        var pipe = ValueOf(args, ElevatedLauncher.PipeSwitch);

        var exitCode = RepairWorker.Run(area, pipe);

        // El worker hace su trabajo y termina: no deja procesos residentes.
        Shutdown(exitCode);
    }

    private void RunAdmin()
    {
        var host = new AppHost();
        var window = new Presentation.AdminWindow();
        window.DataContext = new AdminSettingsViewModel(host, window.Close);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = window;
        window.Show();
    }

    private void RunUserInterface()
    {
        AppPaths.TryEnsureDirectories();

        var host = new AppHost();
        host.Log.Info("Interfaz iniciada. " + SystemChecks.DescribeWindows());

        var window = new Presentation.MainWindow { DataContext = new MainViewModel(host) };

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = window;
        window.Show();
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lee el valor que sigue a un modificador, p. ej. --area gerencia.</summary>
    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
