using System.Windows;

namespace DvdCalibrator.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args is ["--demo"]) window.LoadDemo();
    }
}
