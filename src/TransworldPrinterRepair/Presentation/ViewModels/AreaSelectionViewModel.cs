using System.Collections.ObjectModel;
using System.Windows.Input;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>Una tarjeta de area en la pantalla inicial.</summary>
public sealed class AreaCardViewModel
{
    public AreaCardViewModel(AreaDefinition area, bool isLastUsed, ICommand selectCommand)
    {
        Area = area;
        IsLastUsed = isLastUsed;
        SelectCommand = selectCommand;
    }

    public AreaDefinition Area { get; }
    public string Name => Area.Name;
    public bool IsLastUsed { get; }
    public ICommand SelectCommand { get; }
}

/// <summary>
/// Pantalla 1. Las areas nunca estan escritas en la interfaz: salen de la configuracion
/// embebida en el ejecutable mas los cambios del administrador.
/// </summary>
public sealed class AreaSelectionViewModel : ObservableObject
{
    private readonly AppHost _host;

    public AreaSelectionViewModel(AppHost host, MainViewModel shell)
    {
        _host = host;

        SelectAreaCommand = new RelayCommand(parameter =>
        {
            if (parameter is AreaDefinition area) shell.ShowProblemSelection(area);
        });

        OpenAdminCommand = new RelayCommand(() => _ = shell.OpenAdminAsync());

        Areas = new ObservableCollection<AreaCardViewModel>();
        Load();
    }

    public ObservableCollection<AreaCardViewModel> Areas { get; }
    public ICommand SelectAreaCommand { get; }
    public ICommand OpenAdminCommand { get; }

    public string Title => "¿A qué área perteneces?";

    public string Subtitle =>
        "Selecciona tu área para configurar correctamente la impresora de tu puesto.";

    private void Load()
    {
        Areas.Clear();

        var lastUsed = _host.Configuration.GetLastAreaKey();

        foreach (var area in _host.Config.Areas)
        {
            var isLastUsed = string.Equals(area.Key, lastUsed, StringComparison.OrdinalIgnoreCase);
            Areas.Add(new AreaCardViewModel(area, isLastUsed, SelectAreaCommand));
        }

        _host.Log.Info($"Pantalla de areas cargada con {Areas.Count} opciones.");
    }
}
