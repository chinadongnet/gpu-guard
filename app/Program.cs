using System.Globalization;

namespace GpuGuard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        PinCulture();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: true);

        using var mutex = new Mutex(true, @"Global\GpuGuard.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("GPU Guard 已在运行（托盘右下角）。", "GPU Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);

        GuardEngine? engine = null;
        try
        {
            var cfg = Config.Load();
            engine = new GuardEngine(cfg);
            var form = new MainForm(engine, startMinimized: args.Contains("--minimized"));
            engine.Start();
            Application.Run(form);
        }
        catch (Exception ex) { Report(ex, fatal: true); }
        finally { engine?.Dispose(); }
    }

    /// <summary>
    /// Resolving the user's locale can fail on the first launch after a reboot — at logon the
    /// profile's regional settings are not always readable yet, and a custom or unmapped locale
    /// makes CultureInfo throw CultureNotFoundException. Probe the culture once here and fall
    /// back to invariant, then pin the result on every thread so nothing re-resolves it later.
    /// </summary>
    private static void PinCulture()
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.CurrentCulture;
            _ = culture.NumberFormat;   // forces the culture data to actually load
            _ = culture.DateTimeFormat;
        }
        catch { culture = CultureInfo.InvariantCulture; }

        try
        {
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch { /* invariant is already in effect if this fails */ }
    }

    /// <summary>Appends the full exception to %APPDATA%\GpuGuard\error.log, then shows it.</summary>
    private static void Report(Exception? ex, bool fatal)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Config.Dir);
            File.AppendAllText(Path.Combine(Config.Dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL" : "ERROR")}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
        try { MessageBox.Show(ex.ToString(), fatal ? "GPU Guard 启动失败" : "GPU Guard 错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { }
    }
}
