using System.Windows;

namespace VisualVMs;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // last line of defence: show the error instead of closing the app (and losing an unsaved scheme)
        DispatcherUnhandledException += (_, a) =>
        {
            MessageBox.Show(a.Exception.Message, "Visual VMs", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        base.OnStartup(e);
    }
}
