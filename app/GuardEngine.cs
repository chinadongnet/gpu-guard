namespace GpuGuard;

/// <summary>Which lever the engine is currently holding on one GPU.</summary>
public enum ActiveMode { None, Clock, Power }

/// <summary>One GPU's latest sample and the lever applied to it.</summary>
public sealed record GpuReport(
    int Index,
    GpuState? State,
    ActiveMode Active,
    int CurrentCapMHz,
    int CurrentPowerCapW,
    string LastAction,
    string? LastError,
    string? Notice,
    bool IsThrottling,
    string CapText,
    string ModeText);

/// <summary>
/// Background loop: samples every installed GPU (or one selected GPU) every CheckIntervalSec
/// and, when auto-cooling is on, modulates either the clock ceiling (--lock-gpu-clocks) or
/// the power limit (--power-limit) to keep that card under TargetTempC.
/// Each card has its own lever and cap. The temperature rules are shared.
///
/// Clock locking is the better lever on workstation cards (RTX PRO 4500: 150–200 W power
/// range but 180–3090 MHz clock range) but is refused by many GeForce cards under the
/// Windows WDDM driver (RTX 3090 etc.). Those cards have a wide power range (≈100–350 W+),
/// so ControlMode.Auto probes clock locking once per card and falls back to the power limit.
/// </summary>
public sealed class GuardEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Lane> _lanes = new();
    private Config _cfg;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _loggedSet = "";

    public string? LastError { get; private set; }

    /// <summary>True when auto-cool is on and any card's lever is below its ceiling.</summary>
    public bool IsThrottling
    {
        get { lock (_lock) return _lanes.Values.Any(lane => LaneThrottling(lane)); }
    }

    public event Action? Updated;

    public GuardEngine(Config cfg) { _cfg = cfg; }

    public Config Config { get { lock (_lock) return _cfg; } }

    public IReadOnlyList<GpuReport> Reports
    {
        get { lock (_lock) return _lanes.Values.OrderBy(l => l.Index).Select(ToReport).ToList(); }
    }

    public void ApplyConfig(Config cfg)
    {
        lock (_lock)
        {
            var old = _cfg;
            _cfg = cfg;
            try
            {
                var indices = IndicesFor(cfg);
                if (indices.Length > 0) RetainOnly(indices);

                if (!cfg.AutoCoolEnabled && old.AutoCoolEnabled)
                {
                    foreach (var lane in _lanes.Values) ReleaseLane(lane);
                }
                else if (cfg.ControlMode != old.ControlMode)
                {
                    foreach (var lane in _lanes.Values)
                    {
                        if (lane.Active != ActiveMode.None && !ReleaseLane(lane)) continue;
                        lane.ClockUnsupported = false;
                        lane.Notice = null;
                    }
                }
                else if (cfg.AutoCoolEnabled)
                {
                    foreach (var lane in _lanes.Values)
                    {
                        if (lane.State is not GpuState s) continue;
                        if (lane.Active == ActiveMode.Clock && lane.CurrentCap > ClockCeiling(cfg, s))
                            SetCap(lane, ClockCeiling(cfg, s), s);
                        if (lane.Active == ActiveMode.Power && lane.CurrentPowerCap > PowerCeiling(cfg, s))
                            SetPowerCap(lane, PowerCeiling(cfg, s));
                    }
                }
                LastError = null;
            }
            catch (Exception ex) { LastError = ex.Message; }
        }
        Updated?.Invoke();
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Config cfg;
            lock (_lock) cfg = _cfg;
            try
            {
                if (cfg.ControlAllGpus) TickAll();
                else TickOne(cfg.GpuIndex);
            }
            catch (Exception ex)
            {
                lock (_lock) LastError = ex.Message;
                Log(ex.Message);
            }
            Updated?.Invoke();
            try { Task.Delay(TimeSpan.FromSeconds(Math.Max(1, cfg.CheckIntervalSec)), ct).Wait(ct); }
            catch (OperationCanceledException) { }
            catch (AggregateException) { }
        }
    }

    private void TickAll()
    {
        var states = Nvidia.QueryAll();
        if (states.Length == 0) throw new InvalidOperationException("未检测到 GPU");
        lock (_lock)
        {
            if (!_cfg.ControlAllGpus) return;
            var indices = states.Select(s => s.Index).Distinct().OrderBy(i => i).ToArray();
            RetainOnly(indices);
            NoteSet(indices);
            foreach (var s in states) ApplySample(s);
            LastError = null;
        }
    }

    private void TickOne(int index)
    {
        var s = Nvidia.Query(index);
        lock (_lock)
        {
            if (_cfg.ControlAllGpus || _cfg.GpuIndex != index) return;
            RetainOnly([index]);
            NoteSet([index]);
            ApplySample(s);
            LastError = null;
        }
    }

    /// <summary>Caller holds <see cref="_lock"/>.</summary>
    private void ApplySample(GpuState s)
    {
        var lane = GetLane(s.Index);
        lane.State = s;
        try
        {
            if (!_cfg.AutoCoolEnabled)
            {
                // A failed release keeps the cap so the next tick, and shutdown, can retry.
                if (NeedsRelease(lane) && !ReleaseLane(lane)) return;
                lane.LastError = null;
                lane.LastAction = "off";
                return;
            }
            lane.LastError = null;
            Step(lane, s);
        }
        catch (Exception ex)
        {
            lane.LastError = ex.Message;
            Log($"GPU #{s.Index}: {ex.Message}");
        }
    }

    private int[] IndicesFor(Config cfg)
    {
        if (!cfg.ControlAllGpus) return [cfg.GpuIndex];
        try
        {
            var present = Nvidia.ListIndices();
            if (present.Length > 0) return present;
        }
        catch (Exception ex) { Log("list GPUs failed: " + ex.Message); }
        return _lanes.Count > 0 ? _lanes.Keys.OrderBy(i => i).ToArray() : [cfg.GpuIndex];
    }

    /// <summary>
    /// Release cards that are no longer selected. A card whose reset or power restore failed
    /// stays in the table: the error remains visible, later ticks retry, and shutdown can try again.
    /// Caller holds <see cref="_lock"/>.
    /// </summary>
    private void RetainOnly(int[] indices)
    {
        var keep = indices.ToHashSet();
        foreach (var lane in _lanes.Values.ToList())
        {
            if (keep.Contains(lane.Index)) continue;
            if (ReleaseLane(lane)) _lanes.Remove(lane.Index);
        }
    }

    private void NoteSet(int[] indices)
    {
        var key = string.Join(",", indices);
        if (key == _loggedSet) return;
        _loggedSet = key;
        Log("controlling GPUs: " + key);
    }

    private Lane GetLane(int index)
    {
        if (!_lanes.TryGetValue(index, out var lane))
            _lanes[index] = lane = new Lane { Index = index };
        return lane;
    }

    private bool LaneThrottling(Lane lane)
    {
        if (!_cfg.AutoCoolEnabled || lane.State is not GpuState s) return false;
        return lane.Active switch
        {
            ActiveMode.Clock => lane.CurrentCap > 0 && lane.CurrentCap < ClockCeiling(_cfg, s),
            ActiveMode.Power => lane.CurrentPowerCap > 0 && lane.CurrentPowerCap < PowerCeiling(_cfg, s),
            _ => false,
        };
    }

    private GpuReport ToReport(Lane lane)
    {
        var s = lane.State;
        var cap = lane.Active switch
        {
            ActiveMode.Clock => $"{lane.CurrentCap} MHz  (上限 {(s != null ? ClockCeiling(_cfg, s) : _cfg.ClockCeilingMHz)} MHz, 下限 {(s != null ? ClockFloor(_cfg, s) : _cfg.ClockFloorMHz)} MHz)",
            ActiveMode.Power => $"{lane.CurrentPowerCap} W  (上限 {(s != null ? PowerCeiling(_cfg, s) : _cfg.PowerLimitW)} W, 下限 {(s != null ? PowerFloor(_cfg, s) : _cfg.PowerFloorW)} W)",
            _ => "未干预",
        };
        var cfgMode = _cfg.ControlMode switch { ControlMode.Clock => "锁频", ControlMode.Power => "限功耗", _ => "自动" };
        var act = lane.Active switch { ActiveMode.Clock => "锁频", ActiveMode.Power => "限功耗", _ => _cfg.AutoCoolEnabled ? "待探测" : "—" };
        var hint = lane.ClockUnsupported ? "，本卡不支持锁频" : "";
        return new GpuReport(
            lane.Index, s, lane.Active, lane.CurrentCap, lane.CurrentPowerCap, lane.LastAction,
            lane.LastError, lane.Notice, LaneThrottling(lane), cap, $"{cfgMode} → 实际: {act}{hint}");
    }

    // ---------- effective ranges (config clamped to what the card reports) ----------

    private static int ClockCeiling(Config cfg, GpuState s) =>
        s.MaxClockMHz > 0 ? Math.Min(cfg.ClockCeilingMHz, s.MaxClockMHz) : cfg.ClockCeilingMHz;

    private static int ClockFloor(Config cfg, GpuState s) => Math.Min(cfg.ClockFloorMHz, ClockCeiling(cfg, s));

    private static int ClockLockMin(Config cfg, Lane lane, GpuState s)
    {
        var min = cfg.ClockLockMinMHz;
        if (lane.MinSupportedMHz is int sup && sup > min) min = sup;
        return Math.Min(min, ClockFloor(cfg, s));
    }

    private static int PowerMax(GpuState s) => s.MaxLimitW > 0 ? s.MaxLimitW : Math.Max(s.DefaultLimitW, s.CurrentLimitW);

    private static int PowerCeiling(Config cfg, GpuState s)
    {
        var max = PowerMax(s);
        var def = s.DefaultLimitW > 0 ? s.DefaultLimitW : max;
        var c = cfg.PowerLimitW > 0 ? cfg.PowerLimitW : def;
        return Math.Clamp(c, Math.Max(1, s.MinLimitW), Math.Max(max, s.MinLimitW));
    }

    private static int PowerFloor(Config cfg, GpuState s)
    {
        var f = cfg.PowerFloorW > 0 ? Math.Max(cfg.PowerFloorW, s.MinLimitW) : s.MinLimitW;
        return Math.Min(Math.Max(1, f), PowerCeiling(cfg, s));
    }

    // ---------- control (caller holds _lock) ----------

    private void Step(Lane lane, GpuState s)
    {
        var cfg = _cfg;
        if (lane.Active == ActiveMode.None) Engage(lane, s);

        if (lane.Active == ActiveMode.Clock)
        {
            var floor = ClockFloor(cfg, s);
            var ceiling = ClockCeiling(cfg, s);
            var (desired, action) = Decide(cfg, s.TempC, lane.CurrentCap, floor, ceiling, cfg.StepDownMHz, cfg.StepUpMHz);
            if (desired != lane.CurrentCap) SetCap(lane, desired, s);
            lane.LastAction = action;
        }
        else if (lane.Active == ActiveMode.Power)
        {
            var floor = PowerFloor(cfg, s);
            var ceiling = PowerCeiling(cfg, s);
            var (desired, action) = Decide(cfg, s.TempC, lane.CurrentPowerCap, floor, ceiling, cfg.PowerStepDownW, cfg.PowerStepUpW);
            if (desired != lane.CurrentPowerCap) SetPowerCap(lane, desired);
            lane.LastAction = action;
        }
    }

    private static (int desired, string action) Decide(Config cfg, int temp, int current, int floor, int ceiling, int stepDown, int stepUp)
    {
        if (temp >= cfg.CriticalTempC) return (Math.Max(floor, current - stepDown * 3), "critical-drop");
        if (temp > cfg.TargetTempC) return (Math.Max(floor, current - stepDown), "drop");
        if (temp <= cfg.CoolTempC && current < ceiling) return (Math.Min(ceiling, current + stepUp), "raise");
        return (Math.Clamp(current, floor, ceiling), current == Math.Clamp(current, floor, ceiling) ? "hold" : "clamp");
    }

    private void Engage(Lane lane, GpuState s)
    {
        var cfg = _cfg;
        if (!lane.Probed)
        {
            lane.MinSupportedMHz = Nvidia.MinSupportedGraphicsMHz(lane.Index);
            lane.Probed = true;
            Log($"GPU #{s.Index} {s.Name}: driver={s.DriverModel} maxClock={s.MaxClockMHz}MHz minSupported={lane.MinSupportedMHz?.ToString() ?? "N/A"} power={s.MinLimitW}-{s.MaxLimitW}W default={s.DefaultLimitW}W");
        }

        var mode = cfg.ControlMode;
        if (mode == ControlMode.Auto) mode = lane.ClockUnsupported ? ControlMode.Power : ControlMode.Clock;

        if (mode == ControlMode.Clock)
        {
            try { EngageClock(lane, s); return; }
            catch (Exception ex) when (cfg.ControlMode == ControlMode.Auto)
            {
                lane.ClockUnsupported = true;
                try { Nvidia.ResetClocks(lane.Index); } catch { }
                lane.CurrentCap = 0;
                lane.Notice = $"本卡不支持锁频，已自动切换为限功耗降温。({Shorten(ex.Message)})";
                Log($"GPU #{lane.Index} clock lock unsupported, falling back to power limit: " + ex.Message);
            }
        }

        try { EngagePower(lane, s); }
        catch (Exception ex)
        {
            Log($"GPU #{lane.Index} power limit failed: " + ex.Message);
            if (lane.ClockUnsupported && cfg.ControlMode == ControlMode.Auto)
                throw new InvalidOperationException("锁频和限功耗都失败，本卡/驱动无法通过 nvidia-smi 控制。请确认以管理员运行、驱动为最新版。\n" + ex.Message);
            throw;
        }
    }

    private void EngageClock(Lane lane, GpuState s)
    {
        var cfg = _cfg;
        if (cfg.PowerLimitW > 0 && lane.RestorePowerW == null)
        {
            try
            {
                var pw = Math.Clamp(cfg.PowerLimitW, Math.Max(1, s.MinLimitW), Math.Max(PowerMax(s), s.MinLimitW));
                Nvidia.SetPowerLimit(lane.Index, pw);
                lane.RestorePowerW = s.DefaultLimitW > 0 ? s.DefaultLimitW : PowerMax(s);
            }
            catch (Exception ex)
            {
                lane.Notice = "功耗安全上限设置失败，仅使用锁频: " + Shorten(ex.Message);
                Log($"GPU #{lane.Index} safety power cap failed: " + ex.Message);
            }
        }
        SetCap(lane, ClockCeiling(cfg, s), s);
        lane.Active = ActiveMode.Clock;
        Log($"GPU #{lane.Index} engaged clock lock: {ClockLockMin(cfg, lane, s)}-{lane.CurrentCap} MHz");
    }

    private void EngagePower(Lane lane, GpuState s)
    {
        var cfg = _cfg;
        lane.RestorePowerW ??= s.DefaultLimitW > 0 ? s.DefaultLimitW : PowerMax(s);
        SetPowerCap(lane, PowerCeiling(cfg, s));
        lane.Active = ActiveMode.Power;
        Log($"GPU #{lane.Index} engaged power limit: {lane.CurrentPowerCap} W (range {PowerFloor(cfg, s)}-{PowerCeiling(cfg, s)} W)");
    }

    private void SetCap(Lane lane, int maxMHz, GpuState s)
    {
        Nvidia.LockClocks(lane.Index, ClockLockMin(_cfg, lane, s), maxMHz);
        lane.CurrentCap = maxMHz;
    }

    private void SetPowerCap(Lane lane, int watts)
    {
        Nvidia.SetPowerLimit(lane.Index, watts);
        lane.CurrentPowerCap = watts;
    }

    /// <summary>Returns true only after clocks and the saved power limit are actually restored.</summary>
    private bool ReleaseLane(Lane lane)
    {
        try
        {
            if (lane.CurrentCap != 0)
            {
                Nvidia.ResetClocks(lane.Index);
                lane.CurrentCap = 0;
            }
            if (lane.RestorePowerW is int w)
            {
                Nvidia.SetPowerLimit(lane.Index, w);
                lane.RestorePowerW = null;
            }
            lane.CurrentPowerCap = 0;
            lane.Active = ActiveMode.None;
            lane.LastAction = "off";
            lane.LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            // Leave CurrentCap / RestorePowerW in place so a later call retries the failed step.
            lane.LastError = ex.Message;
            Log($"GPU #{lane.Index} release failed: " + ex.Message);
            return false;
        }
    }

    private static bool NeedsRelease(Lane lane) => lane.CurrentCap != 0 || lane.RestorePowerW != null;

    private static string Shorten(string msg)
    {
        var line = msg.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? msg;
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>Appends a diagnostic line to %APPDATA%\GpuGuard\guard.log (kept under ~1 MB).</summary>
    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            var path = Path.Combine(Config.Dir, "guard.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(3000); } catch { }
        lock (_lock)
        {
            foreach (var lane in _lanes.Values.ToList()) ReleaseLane(lane);
        }
    }

    private sealed class Lane
    {
        public int Index;
        public ActiveMode Active;
        public int CurrentCap;
        public int CurrentPowerCap;
        public int? RestorePowerW;
        public bool ClockUnsupported;
        public bool Probed;
        public int? MinSupportedMHz;
        public GpuState? State;
        public string LastAction = "idle";
        public string? LastError;
        public string? Notice;
    }
}
