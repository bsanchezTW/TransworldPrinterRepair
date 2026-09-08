using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>
/// Shell de navegacion. El orden de las pantallas es fijo y obligatorio:
/// area, problema, confirmacion, progreso, resultado.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private object? _currentPage;

    public MainViewModel(AppHost host)
    {
        _host = host;
        ShowAreaSelection();
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public void ShowAreaSelection() =>
        CurrentPage = new AreaSelectionViewModel(_host, this);

    public void ShowProblemSelection(AreaDefinition area) =>
        CurrentPage = new ProblemSelectionViewModel(this, area);

    public void ShowConfirmation(AreaDefinition area) =>
        CurrentPage = new RepairConfirmationViewModel(this, area);

    public void ShowProgress(AreaDefinition area)
    {
        var progress = new RepairProgressViewModel(_host, this, area);
        CurrentPage = progress;

        // Arranca en cuanto la pantalla es visible; la propia vista muestra el avance real.
        _ = progress.RunAsync();
    }

    public void ShowResult(AreaDefinition area, RepairResult result) =>
        CurrentPage = new RepairResultViewModel(_host, this, area, result);

    /// <summary>
    /// Abre el panel administrativo tras pedir la contrasena. No hace falta elevar: la carpeta
    /// de datos en ProgramData es escribible por el usuario, y lo unico que el panel modifica
    /// son las IPs de las areas.
    /// </summary>
    public void OpenAdmin()
    {
        var owner = System.Windows.Application.Current?.MainWindow;

        var prompt = new PasswordPromptWindow(_host.Log) { Owner = owner };
        if (prompt.ShowDialog() != true) return;

        var admin = new AdminWindow { Owner = owner };
        admin.DataContext = new AdminSettingsViewModel(_host, admin.Close);
        admin.ShowDialog();

        // El administrador pudo cambiar alguna IP: se recarga y se repinta la lista.
        _host.Configuration.Invalidate();
        ShowAreaSelection();
    }
}
