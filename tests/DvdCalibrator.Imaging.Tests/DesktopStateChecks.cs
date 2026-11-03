using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DvdCalibrator.App;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class DesktopStateChecks
{
    // In-process state checks only. No native window, mouse injection, or system dialogs.
    public static void Register(Action<string, Action> test, PhotoTask fitted)
    {
        test("cancelled desktop refit preserves confirmed candidate and clean state", () =>
        {
            var window = View(fitted); var original = fitted.Fit!; string session = RecordJson.Hash(fitted.Session);
            WithDispatcher(() =>
            {
                var pending = window.ComputeTraceFit();
                Check(Button(window, "取消计算").IsEnabled, "cancel unavailable during refit");
                Check(!Button(window, "导出补偿配置").IsEnabled, "export allowed during refit");
                Click(window, "取消计算"); Pump(pending);
            });
            Check(ReferenceEquals(fitted.Fit, original) && fitted.Fit?.IsConfirmed == true, "cancel lost candidate confirmation");
            Check(RecordJson.Hash(fitted.Session) == session && !window.Title.EndsWith(" *", StringComparison.Ordinal), "cancel changed task or dirtied clean input");
            Check(Button(window, "导出补偿配置").IsEnabled && !Button(window, "取消计算").IsEnabled, "controls did not recover");
        });
        test("cancelled desktop rectification preserves geometry confirmation and fit", () =>
        {
            var window = View(fitted); var session = fitted.Session; var fit = fitted.Fit;
            WithDispatcher(() =>
            {
                var pending = (Task)Invoke(window, "Compute")!;
                Click(window, "取消计算"); Pump(pending);
            });
            Check(ReferenceEquals(fitted.Session, session) && fitted.Session.IsConfirmed && ReferenceEquals(fitted.Fit, fit), "cancel partially committed geometry");
            Check(Button(window, "导出补偿配置").IsEnabled && !window.Title.EndsWith(" *", StringComparison.Ordinal), "cancel did not restore original state");
        });
        test("unfinished desktop mask survives mode switch and blocks analysis", () =>
        {
            var window = View(fitted); var mode = Descendants(window.Content).OfType<ComboBox>().Single(c => c.Items.Contains("反光遮罩"));
            mode.SelectedIndex = 2;
            Invoke(window, "SetPoints", ImmutableArray.Create(new PixelPoint(10, 10), new PixelPoint(30, 10), new PixelPoint(30, 30)));
            mode.SelectedIndex = 0;
            Check(mode.SelectedIndex == 2, "mode switch silently discarded mask");
            Check(!Button(window, "拟合描线").IsEnabled && !Button(window, "导出补偿配置").IsEnabled, "unfinished mask allowed analysis/export");
            var fit = fitted.Fit; window.ComputeTraceFit().GetAwaiter().GetResult();
            Check(ReferenceEquals(fitted.Fit, fit), "blocked fit changed prior result");
            // Finish the same visible draft; the committed mask must invalidate the old fit.
            WithDispatcher(() =>
            {
                Click(window, "结束遮罩");
                var idle = new TaskCompletionSource();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) => { if (!Button(window, "取消计算").IsEnabled) idle.TrySetResult(); };
                timer.Start(); try { Pump(idle.Task); } finally { timer.Stop(); }
            });
            Check(fitted.Session.Masks.Length == 1 && fitted.Session.Masks[0].Vertices.Length == 3 && fitted.Fit is null, "draft lost or stale fit retained");
            Check(Button(window, "拟合描线").IsEnabled, "analysis did not resume after mask commit");
        });
    }
    private static MainWindow View(PhotoTask task)
    {
        var window = new MainWindow(); var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1380, 1000)); content.Arrange(new Rect(0, 0, 1380, 1000)); content.UpdateLayout();
        window.LoadPhotoTask(task); return window;
    }
    private static IEnumerable<DependencyObject> Descendants(object parent)
    {
        if (parent is not DependencyObject d) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        { var child = VisualTreeHelper.GetChild(d, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static Button Button(MainWindow window, string name) => Descendants(window.Content).OfType<Button>().Single(b => Equals(b.Content, name));
    private static void Click(MainWindow window, string name) => Button(window, name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static object? Invoke(MainWindow window, string method, params object[] args)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static void WithDispatcher(Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { action(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); bool timedOut = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
        _ = task.ContinueWith(_ => { dispatcher.BeginInvoke(new Action(() => frame.Continue = false)); }, TaskScheduler.Default);
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check(!timedOut, "desktop cancellation did not finish within 30 seconds"); task.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
