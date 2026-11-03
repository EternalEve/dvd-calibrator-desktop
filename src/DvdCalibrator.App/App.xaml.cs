using System.Windows;

namespace DvdCalibrator.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args is ["--demo"]) window.LoadDemo();
        if (e.Args is ["--trace-demo"]) window.LoadTraceDemo();
        if (e.Args is ["--dense-demo"]) window.LoadDenseDemo();
    }
}
