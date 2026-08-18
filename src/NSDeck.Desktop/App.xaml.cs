using System.Windows;

namespace NSDeck.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var designPreview = e.Args.Any(argument => argument.Equals("--design-preview", StringComparison.OrdinalIgnoreCase));
        var changeLabPreview = e.Args.Any(argument => argument.Equals("--change-lab-preview", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(designPreview || changeLabPreview, changeLabPreview);
        MainWindow.Show();
    }
}
