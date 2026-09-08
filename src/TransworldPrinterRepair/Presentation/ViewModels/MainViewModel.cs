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
    /// El panel administrativo no es otra pantalla de este proceso: se abre en una instancia
    /// elevada aparte. Asi el UAC de Windows hace de autenticacion y esa instancia puede
    /// escribir en ProgramData sin que la interfaz del trabajador corra elevada.
    /// </summary>
    public async Task OpenAdminAsync()
    {
        var admin = _host.Launcher.LaunchAdmin();
        if (admin is null) return;   // el usuario rechazo el UAC

        using (admin)
        {
            await admin.WaitForExitAsync().ConfigureAwait(true);
        }

        // El administrador pudo cambiar alguna IP: se recarga y se repinta la lista.
        _host.Configuration.Invalidate();
        ShowAreaSelection();
    }
}
