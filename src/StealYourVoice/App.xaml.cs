using System.Threading;
using System.Windows;

namespace StealYourVoice;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, "StealYourVoice.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Steal Your Voice уже запущен — ищи его в трее.", "Steal Your Voice");
            Shutdown();
            return;
        }

        var w = new MainWindow();
        MainWindow = w;
        bool minimized = false;
        foreach (var a in e.Args)
            if (a == "--tray") minimized = true;
        if (!minimized) w.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
