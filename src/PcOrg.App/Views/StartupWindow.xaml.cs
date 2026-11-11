using System.Windows;
using System.Windows.Threading;

namespace PcOrg.App.Views;

/// <summary>
/// Finestra mostrata subito all'avvio, mentre l'indice viene aperto in background. Così l'utente vede
/// sempre che Gestione File è partito, anche quando l'apertura richiede tempo (conversione dell'indice dopo un
/// aggiornamento, disco lento).
/// </summary>
public partial class StartupWindow : Window
{
    private readonly DispatcherTimer _timer;

    public StartupWindow()
    {
        InitializeComponent();
        // Se dopo qualche secondo si sta ancora aspettando, si spiega il perché.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            StatusText.Text = "Preparazione dei dati…";
            DetailText.Visibility = Visibility.Visible;
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
