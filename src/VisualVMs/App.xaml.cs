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
            // failed before the window came up: nothing to keep alive, do not linger as a windowless process
            if (MainWindow is not { IsLoaded: true }) Shutdown();
        };
        base.OnStartup(e);
    }
}
