using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GpuGuard;

public sealed record GpuState(
    int Index, string Name, int TempC, int ClockSmMHz, double PowerDrawW, string FanPct,
    int UtilPct, int MemUsedMiB, int MemTotalMiB, int MinLimitW, int MaxLimitW, int DefaultLimitW,
    int CurrentLimitW, int MaxClockMHz, string DriverModel);

/// <summary>Thrown when nvidia-smi reports that the GPU/driver does not support a control operation.</summary>
public sealed class NvidiaUnsupportedException(string message) : InvalidOperationException(message);

/// <summary>Thin wrapper over nvidia-smi.exe.</summary>
public static class Nvidia
{
    private static readonly string Exe = FindExe();

    // nvidia-smi frequently reports a failed "set" as a warning and still exits 0
    // ("... is not supported for GPU ... Treating as warning and moving on."), so set
    // commands are judged on their output text as well as their exit code.
    private static readonly Regex FailurePattern = new(
        @"not supported|treating as warning|insufficient permission|does not have permission|terminating early|failed to|are not valid|invalid argument|unknown error",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnsupportedPattern = new(
        @"not supported|treating as warning|deprecated",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    private static (int exit, string output) Exec(params string[] args)
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
        return (p.ExitCode, (stdout + stderr).Trim());
    }

    /// <summary>Query-style call: only the exit code decides success (query output may legitimately contain "[Not Supported]").</summary>
    public static string Run(params string[] args)
    {
        var (exit, output) = Exec(args);
        if (exit != 0) throw new InvalidOperationException($"nvidia-smi 退出码 {exit}: {output}".Trim());
        return output;
    }

    /// <summary>Set-style call: fails on a non-zero exit code or on failure text in the output.</summary>
    private static void RunSet(params string[] args)
    {
        var (exit, output) = Exec(args);
        if (exit == 0 && !FailurePattern.IsMatch(output)) return;
        var msg = $"nvidia-smi {string.Join(' ', args)} 失败 (退出码 {exit}): {output}".Trim();
        if (UnsupportedPattern.IsMatch(output)) throw new NvidiaUnsupportedException(msg);
        throw new InvalidOperationException(msg);
    }

    public static GpuState Query(int gpuIndex)
    {
        const string q = "index,name,temperature.gpu,clocks.sm,power.draw,fan.speed,utilization.gpu,memory.used,memory.total," +
                         "power.min_limit,power.max_limit,power.default_limit,power.limit,clocks.max.sm,driver_model.current";
        // Use -i and --flag=value: nvidia-smi 512.x rejects "--id 0" and space-separated long options.
        var line = Run("-i", gpuIndex.ToString(), $"--query-gpu={q}", "--format=csv,noheader,nounits").Trim();
        var f = line.Split(',').Select(s => s.Trim()).ToArray();
        if (f.Length < 15) throw new InvalidOperationException("nvidia-smi 输出异常: " + line);
        var ci = CultureInfo.InvariantCulture;
        double D(string s) => double.TryParse(s, NumberStyles.Float, ci, out var v) ? v : 0;
        int R(string s) => (int)Math.Round(D(s));
        return new GpuState(
            (int)D(f[0]), f[1], (int)D(f[2]), (int)D(f[3]), D(f[4]), f[5],
            (int)D(f[6]), (int)D(f[7]), (int)D(f[8]),
            R(f[9]), R(f[10]), R(f[11]), R(f[12]), R(f[13]), f[14]);
    }

    /// <summary>
    /// Lowest graphics clock the GPU can be locked to, from "-q -d SUPPORTED_CLOCKS"
    /// (e.g. 180 MHz on Blackwell, 210 MHz on Ampere). Null when the driver reports N/A.
    /// </summary>
    public static int? MinSupportedGraphicsMHz(int gpuIndex)
    {
        try
        {
            var text = Run("-i", gpuIndex.ToString(), "-q", "-d", "SUPPORTED_CLOCKS");
            int? min = null;
            foreach (Match m in Regex.Matches(text, @"Graphics\s*:\s*(\d+)\s*MHz"))
            {
                var v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (min is null || v < min) min = v;
            }
            return min;
        }
        catch { return null; }
    }

    public static void LockClocks(int gpuIndex, int minMHz, int maxMHz) =>
        RunSet("-i", gpuIndex.ToString(), $"--lock-gpu-clocks={minMHz},{maxMHz}");

    public static void ResetClocks(int gpuIndex) =>
        RunSet("-i", gpuIndex.ToString(), "--reset-gpu-clocks");

    public static void SetPowerLimit(int gpuIndex, int watts) =>
        RunSet("-i", gpuIndex.ToString(), $"--power-limit={watts}");
}
