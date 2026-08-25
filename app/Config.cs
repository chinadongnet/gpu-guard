using System.Text.Json;

namespace GpuGuard;

/// <summary>User-adjustable cooling rules + app state. Persisted to %APPDATA%\GpuGuard\config.json.</summary>
public sealed class Config
{
    public int GpuIndex { get; set; } = 0;
    public bool AutoCoolEnabled { get; set; } = true;
    public int TargetTempC { get; set; } = 70;      // drop clocks above this
    public int CoolTempC { get; set; } = 65;        // raise clocks again at/below this
    public int CriticalTempC { get; set; } = 76;    // 3x step drop above this
    public int CheckIntervalSec { get; set; } = 5;
    public int ClockCeilingMHz { get; set; } = 2100;
    public int ClockFloorMHz { get; set; } = 900;
    public int ClockLockMinMHz { get; set; } = 180;
    public int StepDownMHz { get; set; } = 75;
    public int StepUpMHz { get; set; } = 45;
    public int PowerLimitW { get; set; } = 0;       // 0 = untouched
    public string Profile { get; set; } = "normal"; // "cool" | "normal" | "custom"

    /// <summary>Built-in strategies: name -> (target, cool, critical). Clock/step settings are kept.</summary>
    public static readonly (string Key, string Label, int Target, int Cool, int Critical)[] Presets =
    {
        ("cool",   "低温策略（≤60 °C，优先控温）", 60, 55, 66),
        ("normal", "常规策略（≤70 °C，平衡性能）", 70, 65, 76),
    };

    public void ApplyPreset(string key)
    {
        var p = Presets.FirstOrDefault(x => x.Key == key);
        if (p.Key == null) { Profile = "custom"; return; }
        Profile = key; TargetTempC = p.Target; CoolTempC = p.Cool; CriticalTempC = p.Critical;
    }

    /// <summary>Returns the preset key whose temperatures match, else "custom".</summary>
    public string DetectProfile() =>
        Presets.FirstOrDefault(p => p.Target == TargetTempC && p.Cool == CoolTempC && p.Critical == CriticalTempC).Key ?? "custom";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuGuard");
    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpts) ?? new Config();
        }
        catch { /* fall back to defaults on corrupt file */ }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public string? Validate()
    {
        if (TargetTempC <= CoolTempC) return "降温温度必须高于恢复温度。";
        if (CriticalTempC <= TargetTempC) return "紧急温度必须高于降温温度。";
        if (ClockFloorMHz > ClockCeilingMHz) return "频率下限不能高于频率上限。";
        if (ClockLockMinMHz > ClockFloorMHz) return "锁频最低值不能高于频率下限。";
        if (CheckIntervalSec < 1) return "检测间隔至少 1 秒。";
        if (StepDownMHz < 1 || StepUpMHz < 1) return "步进必须大于 0。";
        return null;
    }

    public Config Clone() => (Config)MemberwiseClone();
}
