using System.Windows.Input;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>
/// Pantalla 3. Ultimo punto en el que el usuario puede echarse atras antes de que se
/// eliminen las impresoras del equipo, por eso el aviso es explicito.
/// </summary>
public sealed class RepairConfirmationViewModel : ObservableObject
{
    public RepairConfirmationViewModel(MainViewModel shell, AreaDefinition area)
    {
        Area = area;
        RepairCommand = new RelayCommand(() => shell.ShowProgress(area));
        CancelCommand = new RelayCommand(shell.ShowAreaSelection);
    }

    public AreaDefinition Area { get; }

    public string Title => "Vamos a reparar tu impresora";
    public string AreaName => Area.Name;
    public string PrinterName => Area.PrinterName;
    public string Ip => Area.Ip;

    public string Warning =>
        "Para reparar la impresión, se eliminarán las impresoras actualmente configuradas " +
        "en este equipo y se instalará nuevamente la impresora correspondiente a tu área. " +
        "Se conservarán Microsoft Print to PDF y OneNote.";

    public ICommand RepairCommand { get; }
    public ICommand CancelCommand { get; }
}
