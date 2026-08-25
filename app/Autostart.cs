using System.Diagnostics;

namespace GpuGuard;

/// <summary>
/// Autostart via Task Scheduler (RunLevel=Highest) so the elevated app launches at logon
/// without a UAC prompt — a plain HKCU\Run entry cannot silently start an admin-manifested exe.
/// </summary>
public static class Autostart
{
    private const string TaskName = "GpuGuard";

    public static bool IsEnabled() => Run("schtasks.exe", "/Query", "/TN", TaskName).exit == 0;

    public static void Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位程序路径");
        var r = Run("schtasks.exe", "/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/IT",
                    "/TR", $"\"{exe}\" --minimized");
        if (r.exit != 0) throw new InvalidOperationException("创建计划任务失败: " + r.output);
        // Remove the default "stop after 72h" / "only on AC power" limits.
        const string ps = "$t=Get-ScheduledTask -TaskName 'GpuGuard';$s=$t.Settings;$s.ExecutionTimeLimit='PT0S';$s.DisallowStartIfOnBatteries=$false;$s.StopIfGoingOnBatteries=$false;Set-ScheduledTask -TaskName 'GpuGuard' -Settings $s | Out-Null";
        Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", ps);
    }

    public static void Disable()
    {
        var r = Run("schtasks.exe", "/Delete", "/F", "/TN", TaskName);
        if (r.exit != 0 && IsEnabled())
            throw new InvalidOperationException("删除计划任务失败: " + r.output);
    }

    private static (int exit, string output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }
}
