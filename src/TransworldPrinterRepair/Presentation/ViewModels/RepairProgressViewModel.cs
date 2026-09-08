using System.Collections.ObjectModel;
using System.Windows;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation.ViewModels;

/// <summary>Una de las cinco lineas visibles de la pantalla de progreso.</summary>
public sealed class StageViewModel : ObservableObject
{
    private StepState _state = StepState.Pending;

    public StageViewModel(DisplayStage stage)
    {
        Stage = stage;
        Title = stage.Title();
    }

    public DisplayStage Stage { get; }
    public string Title { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(Glyph));
        }
    }

    public string Glyph => _state switch
    {
        StepState.Ok => "✓",       // marca de verificacion
        StepState.Warning => "!",
        StepState.Failed => "✗",   // aspa
        StepState.Running => "●",  // circulo relleno
        _ => "○",                  // circulo vacio
    };
}

/// <summary>
/// Pantalla 4. Refleja el estado REAL de cada paso segun lo informa el worker elevado.
/// No hay barra de progreso simulada: si un paso tarda, la linea se queda en curso.
/// </summary>
public sealed class RepairProgressViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly MainViewModel _shell;
    private readonly AreaDefinition _area;
    private readonly Dictionary<RepairStep, StepState> _steps = new();

    private string _detail = "Solicitando permisos de Windows...";

    public RepairProgressViewModel(AppHost host, MainViewModel shell, AreaDefinition area)
    {
        _host = host;
        _shell = shell;
        _area = area;

        Stages = new ObservableCollection<StageViewModel>(
            Enum.GetValues<DisplayStage>().Select(s => new StageViewModel(s)));
    }

    public ObservableCollection<StageViewModel> Stages { get; }

    public string Title => "Reparando impresora...";
    public string AreaName => _area.Name;

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public async Task RunAsync()
    {
        // El area elegida se recuerda aunque la reparacion falle: la proxima vez el
        // trabajador la encuentra ya destacada.
        _host.Configuration.SetLastAreaKey(_area.Key);

        var result = await _host.Coordinator
            .RunAsync(_area, OnProgress)
            .ConfigureAwait(true);

        // Una pausa muy corta para que el ultimo tic no desaparezca de golpe.
        await Task.Delay(400).ConfigureAwait(true);

        _shell.ShowResult(_area, result);
    }

    /// <summary>
    /// Llega desde el hilo que lee el pipe, asi que se reenvia al hilo de interfaz.
    /// </summary>
    private void OnProgress(ProgressMessage message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess()) Apply(message);
        else dispatcher.BeginInvoke(new Action(() => Apply(message)));
    }

    private void Apply(ProgressMessage message)
    {
        if (message.Kind != "step" || message.Step is null || message.State is null) return;

        _steps[message.Step.Value] = message.State.Value;

        if (!string.IsNullOrWhiteSpace(message.Detail)) Detail = message.Detail!;
        else if (message.State == StepState.Running) Detail = DescribeRunning(message.Step.Value);

        Recompute();
    }

    private void Recompute()
    {
        foreach (var stage in Stages)
        {
            var members = Enum.GetValues<RepairStep>()
                .Where(step => step.ToStage() == stage.Stage)
                .ToList();

            var known = members.Where(_steps.ContainsKey).Select(m => _steps[m]).ToList();

            stage.State = known switch
            {
                _ when known.Contains(StepState.Failed) => StepState.Failed,
                _ when known.Contains(StepState.Running) => StepState.Running,
                _ when known.Count == 0 => StepState.Pending,
                _ when known.Count < members.Count => StepState.Running,
                _ when known.Contains(StepState.Warning) => StepState.Warning,
                _ => StepState.Ok,
            };
        }
    }

    private static string DescribeRunning(RepairStep step) => step switch
    {
        RepairStep.Preparar => "Comprobando el servicio de impresión...",
        RepairStep.Detectar => "Buscando impresoras instaladas...",
        RepairStep.EliminarImpresoras => "Eliminando impresoras anteriores...",
        RepairStep.LimpiarPuertos => "Limpiando conexiones sobrantes...",
        RepairStep.ExtraerDriver => "Preparando el controlador...",
        RepairStep.InstalarDriver => "Instalando el controlador en Windows...",
        RepairStep.PublicarDriver => "Registrando el controlador...",
        RepairStep.ConfigurarPuerto => "Configurando la conexión con la impresora...",
        RepairStep.CrearImpresora => "Creando la impresora...",
        RepairStep.VerificarConexion => "Comprobando que la impresora responde...",
        _ => "Verificando la configuración...",
    };
}
