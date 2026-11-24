using System;
using System.Threading;
using System.Windows;

namespace StealYourVoice;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // build check: draw the drop zones into a picture and quit
        int ri = Array.IndexOf(e.Args, "--render-zones");
        if (ri >= 0 && ri + 1 < e.Args.Length)
        {
            try
            {
                var counts = new System.Collections.Generic.Dictionary<string, int>
                    { ["tl"] = 2, ["br"] = 1, ["top"] = 3, ["left"] = 0 };
                DockZones.RenderPreview(e.Args[ri + 1], 1920, 1040, "bl", counts);
            }
            catch (Exception ex) { System.IO.File.WriteAllText(e.Args[ri + 1] + ".err", ex.ToString()); }
            Shutdown();
            return;
        }

        bool updated = Array.IndexOf(e.Args, "--updated") >= 0;

        _single = new Mutex(true, "StealYourVoice.SingleInstance", out bool created);
        if (!created && updated)
        {
            // the old version is still shutting down — wait for it to release the mutex
            try { created = _single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (updated) Updater.CleanupOld();

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
