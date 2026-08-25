namespace GpuGuard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\GpuGuard.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("GPU Guard 已在运行（托盘右下角）。", "GPU Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.ToString(), "GPU Guard 错误");

        var cfg = Config.Load();
        var engine = new GuardEngine(cfg);
        var form = new MainForm(engine, startMinimized: args.Contains("--minimized"));
        engine.Start();
        Application.Run(form);
        engine.Dispose();
    }
}
