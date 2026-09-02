namespace GpuGuard;

/// <summary>Which lever the engine is currently holding.</summary>
public enum ActiveMode { None, Clock, Power }

/// <summary>
/// Background loop: samples the GPU every CheckIntervalSec and, when auto-cooling is on,
/// modulates either the GPU clock ceiling (--lock-gpu-clocks) or the power limit
/// (--power-limit) to keep temperature under TargetTempC.
///
/// Clock locking is the better lever on workstation cards (RTX PRO 4500: 150–200 W power
/// range but 180–3090 MHz clock range) but is refused by many GeForce cards under the
/// Windows WDDM driver (RTX 3090 etc.). Those cards have a wide power range (≈100–350 W+),
/// so ControlMode.Auto probes clock locking once and falls back to the power limit.
/// </summary>
public sealed class GuardEngine : IDisposable
{
    private readonly object _lock = new();
    private Config _cfg;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private ActiveMode _active = ActiveMode.None;
    private int _currentCap;          // MHz, clock mode; 0 = clocks not locked
    private int _currentPowerCap;     // W, power mode; 0 = not set
    private int? _restorePowerW;      // power limit to put back on release
    private bool _clockUnsupported;   // learned for the current GPU index this session
    private int _probedGpu = -1;
    private int? _minSupportedMHz;

    public GpuState? LastState { get; private set; }
    public string LastAction { get; private set; } = "idle";
    public string? LastError { get; private set; }
    /// <summary>Non-fatal information for the UI, e.g. "clock locking unsupported, using power limit".</summary>
    public string? Notice { get; private set; }

    public ActiveMode Active { get { lock (_lock) return _active; } }
    public int CurrentCapMHz => _currentCap;
    public int CurrentPowerCapW => _currentPowerCap;

    /// <summary>True when auto-cool is on and the active lever is below its ceiling (actively throttling).</summary>
    public bool IsThrottling
    {
        get
        {
            lock (_lock)
            {
                if (!_cfg.AutoCoolEnabled || LastState is not GpuState s) return false;
                return _active switch
                {
                    ActiveMode.Clock => _currentCap > 0 && _currentCap < ClockCeiling(_cfg, s),
                    ActiveMode.Power => _currentPowerCap > 0 && _currentPowerCap < PowerCeiling(_cfg, s),
                    _ => false,
                };
            }
        }
    }

    /// <summary>Human-readable "current cap" line for the panel.</summary>
    public string CapText
    {
        get
        {
            lock (_lock)
            {
                var s = LastState;
                return _active switch
                {
                    ActiveMode.Clock => $"{_currentCap} MHz  (上限 {(s != null ? ClockCeiling(_cfg, s) : _cfg.ClockCeilingMHz)} MHz, 下限 {(s != null ? ClockFloor(_cfg, s) : _cfg.ClockFloorMHz)} MHz)",
                    ActiveMode.Power => $"{_currentPowerCap} W  (上限 {(s != null ? PowerCeiling(_cfg, s) : _cfg.PowerLimitW)} W, 下限 {(s != null ? PowerFloor(_cfg, s) : _cfg.PowerFloorW)} W)",
                    _ => "未干预",
                };
            }
        }
    }

    /// <summary>Human-readable control-mode line for the panel.</summary>
    public string ModeText
    {
        get
        {
            lock (_lock)
            {
                var cfgMode = _cfg.ControlMode switch { ControlMode.Clock => "锁频", ControlMode.Power => "限功耗", _ => "自动" };
                var act = _active switch { ActiveMode.Clock => "锁频", ActiveMode.Power => "限功耗", _ => _cfg.AutoCoolEnabled ? "待探测" : "—" };
                var hint = _clockUnsupported ? "，本卡不支持锁频" : "";
                return $"{cfgMode} → 实际: {act}{hint}";
            }
        }
    }

    public event Action? Updated;

    public GuardEngine(Config cfg) { _cfg = cfg; }

    public Config Config { get { lock (_lock) return _cfg; } }

    public void ApplyConfig(Config cfg)
    {
        lock (_lock)
        {
            var old = _cfg;
            _cfg = cfg;
            try
            {
                if (cfg.GpuIndex != old.GpuIndex)
                {
                    // Different card: drop everything we learned and release the old one.
                    if (_active != ActiveMode.None) ReleaseWith(old);
                    _clockUnsupported = false; _minSupportedMHz = null; _probedGpu = -1; Notice = null;
                }
                else if (!cfg.AutoCoolEnabled && old.AutoCoolEnabled) ReleaseClocks();
                else if (cfg.ControlMode != old.ControlMode && _active != ActiveMode.None)
                {
                    // Mode switched while running: release now, the next tick re-engages with the new mode.
                    ReleaseClocks();
                    if (cfg.ControlMode != ControlMode.Auto) _clockUnsupported = false;
                }
                else if (cfg.AutoCoolEnabled && LastState is GpuState s)
                {
                    if (_active == ActiveMode.Clock && _currentCap > ClockCeiling(cfg, s)) SetCap(ClockCeiling(cfg, s), s);
                    if (_active == ActiveMode.Power && _currentPowerCap > PowerCeiling(cfg, s)) SetPowerCap(PowerCeiling(cfg, s));
                }
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
                var s = Nvidia.Query(cfg.GpuIndex);
                LastState = s;
                LastError = null;
                if (cfg.AutoCoolEnabled) Step(cfg, s);
                else LastAction = "off";
            }
            catch (Exception ex) { LastError = ex.Message; }
            Updated?.Invoke();
            try { Task.Delay(TimeSpan.FromSeconds(Math.Max(1, cfg.CheckIntervalSec)), ct).Wait(ct); }
            catch (OperationCanceledException) { }
            catch (AggregateException) { }
        }
    }

    // ---------- effective ranges (config clamped to what the card reports) ----------

    private int ClockCeiling(Config cfg, GpuState s) =>
        s.MaxClockMHz > 0 ? Math.Min(cfg.ClockCeilingMHz, s.MaxClockMHz) : cfg.ClockCeilingMHz;

    private int ClockFloor(Config cfg, GpuState s) => Math.Min(cfg.ClockFloorMHz, ClockCeiling(cfg, s));

    private int ClockLockMin(Config cfg, GpuState s)
    {
        var min = cfg.ClockLockMinMHz;
        if (_minSupportedMHz is int sup && sup > min) min = sup;   // e.g. 210 MHz on RTX 30 series
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

    // ---------- control ----------

    private void Step(Config cfg, GpuState s)
    {
        lock (_lock)
        {
            if (_active == ActiveMode.None) Engage(cfg, s);

            if (_active == ActiveMode.Clock)
            {
                var floor = ClockFloor(cfg, s); var ceiling = ClockCeiling(cfg, s);
                var (desired, action) = Decide(cfg, s.TempC, _currentCap, floor, ceiling, cfg.StepDownMHz, cfg.StepUpMHz);
                if (desired != _currentCap) SetCap(desired, s);
                LastAction = action;
            }
            else if (_active == ActiveMode.Power)
            {
                var floor = PowerFloor(cfg, s); var ceiling = PowerCeiling(cfg, s);
                var (desired, action) = Decide(cfg, s.TempC, _currentPowerCap, floor, ceiling, cfg.PowerStepDownW, cfg.PowerStepUpW);
                if (desired != _currentPowerCap) SetPowerCap(desired);
                LastAction = action;
            }
        }
    }

    private static (int desired, string action) Decide(Config cfg, int temp, int current, int floor, int ceiling, int stepDown, int stepUp)
    {
        if (temp >= cfg.CriticalTempC) return (Math.Max(floor, current - stepDown * 3), "critical-drop");
        if (temp > cfg.TargetTempC) return (Math.Max(floor, current - stepDown), "drop");
        if (temp <= cfg.CoolTempC && current < ceiling) return (Math.Min(ceiling, current + stepUp), "raise");
        return (Math.Clamp(current, floor, ceiling), current == Math.Clamp(current, floor, ceiling) ? "hold" : "clamp");
    }

    /// <summary>First tick after enabling: pick and engage a lever, honouring ControlMode and what the card supports.</summary>
    private void Engage(Config cfg, GpuState s)
    {
        if (_probedGpu != cfg.GpuIndex)
        {
            _minSupportedMHz = Nvidia.MinSupportedGraphicsMHz(cfg.GpuIndex);
            _probedGpu = cfg.GpuIndex;
            Log($"GPU #{s.Index} {s.Name}: driver={s.DriverModel} maxClock={s.MaxClockMHz}MHz minSupported={_minSupportedMHz?.ToString() ?? "N/A"} power={s.MinLimitW}-{s.MaxLimitW}W default={s.DefaultLimitW}W");
        }

        var mode = cfg.ControlMode;
        if (mode == ControlMode.Auto) mode = _clockUnsupported ? ControlMode.Power : ControlMode.Clock;

        if (mode == ControlMode.Clock)
        {
            try { EngageClock(cfg, s); return; }
            catch (Exception ex) when (cfg.ControlMode == ControlMode.Auto)
            {
                // Auto: the card refused clock locking -> remember and try the power limit instead.
                _clockUnsupported = true;
                try { Nvidia.ResetClocks(cfg.GpuIndex); } catch { }
                _currentCap = 0;
                Notice = $"本卡不支持锁频，已自动切换为限功耗降温。({Shorten(ex.Message)})";
                Log("clock lock unsupported, falling back to power limit: " + ex.Message);
            }
        }

        try { EngagePower(cfg, s); }
        catch (Exception ex)
        {
            Log("power limit failed: " + ex.Message);
            if (_clockUnsupported && cfg.ControlMode == ControlMode.Auto)
                throw new InvalidOperationException("锁频和限功耗都失败，本卡/驱动无法通过 nvidia-smi 控制。请确认以管理员运行、驱动为最新版。\n" + ex.Message);
            throw;
        }
    }

    private void EngageClock(Config cfg, GpuState s)
    {
        // Optional one-time power-limit safety cap; not fatal if the card refuses it.
        if (cfg.PowerLimitW > 0 && _restorePowerW == null)
        {
            try
            {
                var pw = Math.Clamp(cfg.PowerLimitW, Math.Max(1, s.MinLimitW), Math.Max(PowerMax(s), s.MinLimitW));
                Nvidia.SetPowerLimit(cfg.GpuIndex, pw);
                _restorePowerW = s.DefaultLimitW > 0 ? s.DefaultLimitW : PowerMax(s);
            }
            catch (Exception ex) { Notice = "功耗安全上限设置失败，仅使用锁频: " + Shorten(ex.Message); Log("safety power cap failed: " + ex.Message); }
        }
        SetCap(ClockCeiling(cfg, s), s);
        _active = ActiveMode.Clock;
        Log($"engaged clock lock: {ClockLockMin(cfg, s)}-{_currentCap} MHz");
    }

    private void EngagePower(Config cfg, GpuState s)
    {
        _restorePowerW ??= s.DefaultLimitW > 0 ? s.DefaultLimitW : PowerMax(s);
        SetPowerCap(PowerCeiling(cfg, s));
        _active = ActiveMode.Power;
        Log($"engaged power limit: {_currentPowerCap} W (range {PowerFloor(cfg, s)}-{PowerCeiling(cfg, s)} W)");
    }

    private void SetCap(int maxMHz, GpuState s)
    {
        Nvidia.LockClocks(_cfg.GpuIndex, ClockLockMin(_cfg, s), maxMHz);
        _currentCap = maxMHz;
    }

    private void SetPowerCap(int watts)
    {
        Nvidia.SetPowerLimit(_cfg.GpuIndex, watts);
        _currentPowerCap = watts;
    }

    private void ReleaseClocks() => ReleaseWith(_cfg);

    private void ReleaseWith(Config cfg)
    {
        try
        {
            if (_currentCap != 0) Nvidia.ResetClocks(cfg.GpuIndex);
            if (_restorePowerW is int w) { Nvidia.SetPowerLimit(cfg.GpuIndex, w); _restorePowerW = null; }
        }
        catch (Exception ex) { LastError = ex.Message; Log("release failed: " + ex.Message); }
        _currentCap = 0;
        _currentPowerCap = 0;
        _active = ActiveMode.None;
        LastAction = "off";
    }

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
        lock (_lock) ReleaseClocks();
    }
}
