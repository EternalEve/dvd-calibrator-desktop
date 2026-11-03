using System.IO;
using System.Windows;

namespace DvdCalibrator.App;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--verify-installation")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = 0;
            try
            {
                if (e.Args is not ["--verify-installation", "--out", var output])
                    throw new ArgumentException("用法：--verify-installation --out <尚不存在的目录>");
                InstallationCheck.Run(output);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException
                or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
            { Console.Error.WriteLine(error); code = 2; }
            Shutdown(code); return;
        }
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args is ["--demo"]) window.LoadDemo();
        if (e.Args is ["--trace-demo"]) window.LoadTraceDemo();
        if (e.Args is ["--dense-demo"]) window.LoadDenseDemo();
        if (e.Args is ["--two-disc-demo"]) await window.LoadTwoDiscDemo();
    }
}
