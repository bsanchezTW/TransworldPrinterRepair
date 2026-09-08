using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>
/// Pantalla 5. Muestra el desenlace en lenguaje llano. Los codigos tecnicos
/// (HRESULT, exit codes) nunca aparecen aqui: viven en el archivo de log.
/// </summary>
public sealed class RepairResultViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly RepairResult _result;
    private string? _testPageStatus;

    public RepairResultViewModel(AppHost host, MainViewModel shell, AreaDefinition area, RepairResult result)
    {
        _host = host;
        _result = result;
        Area = area;

        FinishCommand = new RelayCommand(() => System.Windows.Application.Current.Shutdown());
        RetryCommand = new RelayCommand(() => shell.ShowProgress(area));
        BackCommand = new RelayCommand(shell.ShowAreaSelection);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        PrintTestPageCommand = new RelayCommand(PrintTestPage);

        if (result.Success)
            host.Log.Info($"Resultado mostrado al usuario: correcto ({result.PrinterName}).");
        else
            host.Log.Warn($"Resultado mostrado al usuario: fallo {result.ErrorCode}. Detalle: {result.ErrorDetail}");
    }

    public AreaDefinition Area { get; }

    public bool Success => _result.Success;
    public bool Failed => !_result.Success;

    public string PrinterName => _result.PrinterName ?? Area.PrinterName;

    public string Title => Success
        ? "¡Impresora configurada!"
        : "No pudimos completar la reparación";

    public string Message => Success
        ? "La impresora de tu área está lista para utilizarse y quedó como predeterminada."
        : _result.ErrorCode.Friendly(Area.Ip, PrinterName);

    public string? TestPageStatus
    {
        get => _testPageStatus;
        private set => SetProperty(ref _testPageStatus, value);
    }

    public ICommand FinishCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand PrintTestPageCommand { get; }

    private void OpenLogs()
    {
        try
        {
            var folder = AppPaths.LogsDirectory;
            if (!Directory.Exists(folder)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _host.Log.Warn("No se pudo abrir la carpeta de logs: " + LoggingService.Describe(ex));
        }
    }

    /// <summary>
    /// Imprime una pagina de prueba a traves del driver recien instalado. Es la unica
    /// comprobacion que confirma la cadena completa: spooler, driver, puerto y hardware.
    /// </summary>
    private void PrintTestPage()
    {
        try
        {
            TestPageStatus = "Enviando página de prueba...";

            var queue = new PrintQueue(new LocalPrintServer(), PrinterName);
            var dialog = new PrintDialog { PrintQueue = queue };

            var width = dialog.PrintableAreaWidth > 0 ? dialog.PrintableAreaWidth : 793;
            var height = dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : 1122;
            var size = new Size(width, height);

            var page = BuildTestPage();
            page.Measure(size);
            page.Arrange(new Rect(new Point(0, 0), size));
            page.UpdateLayout();

            dialog.PrintVisual(page, "Página de prueba - Transworld");

            TestPageStatus = "Página de prueba enviada a " + PrinterName + ".";
            _host.Log.Info("Pagina de prueba enviada a " + PrinterName);
        }
        catch (Exception ex)
        {
            TestPageStatus = "No se pudo enviar la página de prueba.";
            _host.Log.Error("Fallo al imprimir la pagina de prueba.", ex);
        }
    }

    private FrameworkElement BuildTestPage()
    {
        var stack = new StackPanel { Margin = new Thickness(60) };

        stack.Children.Add(new TextBlock
        {
            Text = "Página de prueba",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        stack.Children.Add(new TextBlock
        {
            Text = "AutoReparación de Impresoras Transworld",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 30),
        });

        AddRow(stack, "Área", Area.Name);
        AddRow(stack, "Impresora", PrinterName);
        AddRow(stack, "Dirección IP", Area.Ip);
        AddRow(stack, "Fecha", DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        AddRow(stack, "Equipo", Environment.MachineName);

        stack.Children.Add(new TextBlock
        {
            Text = "Si puedes leer esta página, la impresora está correctamente configurada.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 30, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border { Background = Brushes.White, Child = stack };
    }

    private static void AddRow(Panel parent, string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 140,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = Brushes.Gray,
        });

        row.Children.Add(new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        parent.Children.Add(row);
    }
}
