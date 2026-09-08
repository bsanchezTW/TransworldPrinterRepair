using System.Windows.Input;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>
/// Pantalla 2. De momento solo existe un problema. La pantalla esta preparada para admitir
/// mas opciones sin cambiar el diseno, pero no se implementan hasta que se pidan.
/// </summary>
public sealed class ProblemSelectionViewModel : ObservableObject
{
    public ProblemSelectionViewModel(MainViewModel shell, AreaDefinition area)
    {
        Area = area;
        ContinueCommand = new RelayCommand(() => shell.ShowConfirmation(area));
        BackCommand = new RelayCommand(shell.ShowAreaSelection);
    }

    public AreaDefinition Area { get; }

    public string Title => "¿Qué problema tienes?";
    public string Subtitle => $"Área seleccionada: {Area.Name}";

    public string ProblemTitle => "No puedo imprimir";
    public string ProblemDescription => "Te ayudaremos a configurar nuevamente la impresora de tu área.";

    public ICommand ContinueCommand { get; }
    public ICommand BackCommand { get; }
}
