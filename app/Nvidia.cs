using System.Diagnostics;
using System.Globalization;

namespace GpuGuard;

public sealed record GpuState(
    int Index, string Name, int TempC, int ClockSmMHz, double PowerDrawW, string FanPct,
    int UtilPct, int MemUsedMiB, int MemTotalMiB, int MinLimitW, int MaxLimitW, int DefaultLimitW);

/// <summary>Thin wrapper over nvidia-smi.exe.</summary>
public static class Nvidia
{
    private static readonly string Exe = FindExe();

    private static string FindExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = Path.Combine(dir.Trim(), "nvidia-smi.exe");
            if (File.Exists(p)) return p;
        }
        var sys = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (File.Exists(sys)) return sys;
        var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
        if (File.Exists(nvsmi)) return nvsmi;
        throw new FileNotFoundException("未找到 nvidia-smi.exe，请安装 NVIDIA 驱动。");
    }

    public static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"nvidia-smi 退出码 {p.ExitCode}: {stdout}{stderr}".Trim());
        return stdout;
    }

    public static GpuState Query(int gpuIndex)
    {
        const string q = "index,name,temperature.gpu,clocks.sm,power.draw,fan.speed,utilization.gpu,memory.used,memory.total,power.min_limit,power.max_limit,power.default_limit";
        var line = Run("--id", gpuIndex.ToString(), $"--query-gpu={q}", "--format=csv,noheader,nounits").Trim();
        var f = line.Split(',').Select(s => s.Trim()).ToArray();
        if (f.Length < 12) throw new InvalidOperationException("nvidia-smi 输出异常: " + line);
        var ci = CultureInfo.InvariantCulture;
        double D(string s) => double.TryParse(s, NumberStyles.Float, ci, out var v) ? v : 0;
        return new GpuState(
            (int)D(f[0]), f[1], (int)D(f[2]), (int)D(f[3]), D(f[4]), f[5],
            (int)D(f[6]), (int)D(f[7]), (int)D(f[8]),
            (int)Math.Round(D(f[9])), (int)Math.Round(D(f[10])), (int)Math.Round(D(f[11])));
    }

    public static void LockClocks(int gpuIndex, int minMHz, int maxMHz) =>
        Run("--id", gpuIndex.ToString(), "--lock-gpu-clocks", $"{minMHz},{maxMHz}");

    public static void ResetClocks(int gpuIndex) =>
        Run("--id", gpuIndex.ToString(), "--reset-gpu-clocks");

    public static void SetPowerLimit(int gpuIndex, int watts) =>
        Run("--id", gpuIndex.ToString(), "--power-limit", watts.ToString());
}
