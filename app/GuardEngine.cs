namespace GpuGuard;

/// <summary>
/// Background loop: samples the GPU every CheckIntervalSec and, when auto-cooling is on,
/// modulates the GPU clock ceiling to keep temperature under TargetTempC.
/// Port of gpu-temp-guard.ps1.
/// </summary>
public sealed class GuardEngine : IDisposable
{
    private readonly object _lock = new();
    private Config _cfg;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _currentCap;      // 0 = clocks not locked
    private int? _restorePowerW;

    public GpuState? LastState { get; private set; }
    public string LastAction { get; private set; } = "idle";
    public string? LastError { get; private set; }
    public int CurrentCapMHz => _currentCap;

    /// <summary>True when auto-cool is on and the clock cap is below the ceiling (actively throttling).</summary>
    public bool IsThrottling { get { lock (_lock) return _cfg.AutoCoolEnabled && _currentCap > 0 && _currentCap < _cfg.ClockCeilingMHz; } }

    public event Action? Updated;

    public GuardEngine(Config cfg) { _cfg = cfg; }

    public Config Config { get { lock (_lock) return _cfg; } }

    public void ApplyConfig(Config cfg)
    {
        lock (_lock)
        {
            var wasEnabled = _cfg.AutoCoolEnabled;
            _cfg = cfg;
            try
            {
                if (!cfg.AutoCoolEnabled && wasEnabled) ReleaseClocks();
                else if (cfg.AutoCoolEnabled && _currentCap > cfg.ClockCeilingMHz) SetCap(cfg.ClockCeilingMHz);
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

    private void Step(Config cfg, GpuState s)
    {
        lock (_lock)
        {
            if (_currentCap == 0)
            {
                // First enable: optional power-limit safety cap, then start at ceiling.
                if (cfg.PowerLimitW > 0 && _restorePowerW == null)
                {
                    var pw = Math.Clamp(cfg.PowerLimitW, s.MinLimitW, s.MaxLimitW);
                    Nvidia.SetPowerLimit(cfg.GpuIndex, pw);
                    _restorePowerW = s.DefaultLimitW;
                }
                SetCap(cfg.ClockCeilingMHz);
            }

            var desired = _currentCap;
            var action = "hold";
            if (s.TempC >= cfg.CriticalTempC) { desired = Math.Max(cfg.ClockFloorMHz, _currentCap - cfg.StepDownMHz * 3); action = "critical-drop"; }
            else if (s.TempC > cfg.TargetTempC) { desired = Math.Max(cfg.ClockFloorMHz, _currentCap - cfg.StepDownMHz); action = "drop"; }
            else if (s.TempC <= cfg.CoolTempC && _currentCap < cfg.ClockCeilingMHz) { desired = Math.Min(cfg.ClockCeilingMHz, _currentCap + cfg.StepUpMHz); action = "raise"; }

            if (desired != _currentCap) SetCap(desired);
            LastAction = action;
        }
    }

    private void SetCap(int maxMHz)
    {
        Nvidia.LockClocks(_cfg.GpuIndex, _cfg.ClockLockMinMHz, maxMHz);
        _currentCap = maxMHz;
    }

    private void ReleaseClocks()
    {
        try
        {
            if (_currentCap != 0) Nvidia.ResetClocks(_cfg.GpuIndex);
            if (_restorePowerW is int w) { Nvidia.SetPowerLimit(_cfg.GpuIndex, w); _restorePowerW = null; }
        }
        catch (Exception ex) { LastError = ex.Message; }
        _currentCap = 0;
        LastAction = "off";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(3000); } catch { }
        lock (_lock) ReleaseClocks();
    }
}
