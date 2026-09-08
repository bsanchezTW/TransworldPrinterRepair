using System.Windows;
using System.Windows.Input;
using Transworld.PrinterRepair.Services;

namespace Transworld.PrinterRepair.Presentation;

/// <summary>
/// Pide la contrasena que protege el panel administrativo.
///
/// Tras tres intentos fallidos se cierra: no es una defensa seria (ver AdminPassword), pero
/// evita que alguien pruebe combinaciones delante del equipo sin que quede rastro, porque
/// cada intento se registra en el log.
/// </summary>
public partial class PasswordPromptWindow : Window
{
    private const int MaxAttempts = 3;

    private readonly LoggingService _log;
    private int _attempts;

    public PasswordPromptWindow(LoggingService log)
    {
        InitializeComponent();
        _log = log;
        Loaded += (_, _) => Password.Focus();
    }

    private void OnAccept(object sender, RoutedEventArgs e) => Check();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _log.Info("Acceso al panel administrativo cancelado por el usuario.");
        DialogResult = false;
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Check();
    }

    private void Check()
    {
        if (AdminPassword.Verify(Password.Password))
        {
            _log.Info("Acceso al panel administrativo concedido.");
            DialogResult = true;
            return;
        }

        _attempts++;
        Password.Clear();
        Password.Focus();

        _log.Warn($"Contrasena administrativa incorrecta (intento {_attempts} de {MaxAttempts}).");

        if (_attempts >= MaxAttempts)
        {
            _log.Warn("Acceso al panel administrativo bloqueado tras agotar los intentos.");
            ErrorText.Text = "Demasiados intentos fallidos.";
            ErrorText.Visibility = Visibility.Visible;
            DialogResult = false;
            return;
        }

        ErrorText.Text = $"Contraseña incorrecta. Te quedan {MaxAttempts - _attempts} intentos.";
        ErrorText.Visibility = Visibility.Visible;
    }
}
