namespace GpuGuard;

/// <summary>Tray-resident main window: live GPU data, auto-cool toggle, rule settings, autostart.</summary>
public sealed class MainForm : Form
{
    private readonly GuardEngine _engine;
    private readonly NotifyIcon _tray;
    private Icon? _trayIcon;

    private readonly FlowLayoutPanel _liveHost = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top, MinimumSize = new Size(420, 0),
    };
    private readonly Label _lblPlaceholder = new() { AutoSize = true, ForeColor = Color.DimGray, Text = "正在读取显卡…" };
    private readonly Label _lblError = new() { ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Dictionary<int, GpuCardUi> _cards = new();

    private readonly CheckBox _chkAuto = new() { Text = "启用自动 GPU 降温", AutoSize = true, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
    private readonly CheckBox _chkAll = new() { Text = "控制全部 GPU（各卡按自己的温度，共用一套规则）", AutoSize = true };
    private readonly CheckBox _chkAutostart = new() { Text = "开机自动启动（计划任务，管理员权限）", AutoSize = true };
    private readonly ToolStripMenuItem _menuAuto = new("自动降温");
    private readonly ToolStripMenuItem _menuProfile = new("降温策略");
    private readonly ComboBox _cmbProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _cmbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };

    // Rule settings
    private readonly NumericUpDown _numGpu = Num(0, 15);
    private readonly NumericUpDown _numTarget = Num(30, 100);
    private readonly NumericUpDown _numCool = Num(20, 100);
    private readonly NumericUpDown _numCritical = Num(30, 110);
    private readonly NumericUpDown _numInterval = Num(1, 120);
    private readonly NumericUpDown _numCeiling = Num(180, 4000);
    private readonly NumericUpDown _numFloor = Num(180, 4000);
    private readonly NumericUpDown _numLockMin = Num(100, 4000);
    private readonly NumericUpDown _numStepDown = Num(1, 1000);
    private readonly NumericUpDown _numStepUp = Num(1, 1000);
    private readonly NumericUpDown _numPower = Num(0, 1000);
    private readonly NumericUpDown _numPowerFloor = Num(0, 1000);
    private readonly NumericUpDown _numPowerStepDown = Num(1, 200);
    private readonly NumericUpDown _numPowerStepUp = Num(1, 200);

    private bool _loadingUi;

    private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 80 };

    public MainForm(GuardEngine engine, bool startMinimized)
    {
        _engine = engine;
        Text = "GPU Guard — GPU 温度守护";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 900);
        ShowInTaskbar = false;

        BuildLayout();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("打开面板", null, (_, _) => ShowPanel()));
        _menuAuto.CheckOnClick = true;
        _menuAuto.Click += (_, _) => { _chkAuto.Checked = _menuAuto.Checked; };
        menu.Items.Add(_menuAuto);
        foreach (var p in Config.Presets)
        {
            var item = new ToolStripMenuItem(p.Label) { Tag = p.Key };
            item.Click += (_, _) => { _cmbProfile.SelectedIndex = Array.FindIndex(Config.Presets, x => x.Key == p.Key); };
            _menuProfile.DropDownItems.Add(item);
        }
        menu.Items.Add(_menuProfile);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "GPU Guard" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };

        LoadConfigToUi(_engine.Config);
        RefreshTray();

        _engine.Updated += () => { try { BeginInvoke(RefreshAll); } catch { } };

        if (startMinimized) { Opacity = 0; Load += (_, _) => { Hide(); Opacity = 1; }; }
        else PositionNearTray();
    }

    // ---------- layout ----------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoScroll = true };
        Controls.Add(root);

        // Live data — one card per GPU, rebuilt as the set changes.
        var live = new GroupBox { Text = "GPU 实时数据", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        _liveHost.Controls.Add(_lblPlaceholder);
        _liveHost.Controls.Add(_lblError);
        live.Controls.Add(_liveHost);
        root.Controls.Add(live);

        // Toggle
        var ctl = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 8, 0, 8) };
        _chkAuto.CheckedChanged += (_, _) => { if (!_loadingUi) OnAutoToggled(); };
        _chkAutostart.CheckedChanged += (_, _) => { if (!_loadingUi) OnAutostartToggled(); };
        ctl.Controls.Add(_chkAuto);
        ctl.Controls.Add(_chkAutostart);
        root.Controls.Add(ctl);

        // Rules
        var rules = new GroupBox { Text = "降温规则", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var rt = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top };
        rt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        rt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        rt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        rt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        rt.Controls.Add(new Label { Text = "策略", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
        foreach (var p in Config.Presets) _cmbProfile.Items.Add(p.Label);
        _cmbProfile.Items.Add("自定义（手动设置温度）");
        _cmbProfile.SelectedIndexChanged += (_, _) => { if (!_loadingUi) OnProfileChanged(); };
        rt.Controls.Add(_cmbProfile); rt.SetColumnSpan(_cmbProfile, 3);
        rt.Controls.Add(new Label { Text = "控制方式", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
        foreach (var m in Config.ControlModes) _cmbMode.Items.Add(m.Label);
        rt.Controls.Add(_cmbMode); rt.SetColumnSpan(_cmbMode, 3);
        _chkAll.Margin = new Padding(0, 8, 0, 0);
        _chkAll.CheckedChanged += (_, _) => { if (!_loadingUi) OnAllGpusToggled(); };
        rt.Controls.Add(_chkAll); rt.SetColumnSpan(_chkAll, 4);
        AddPair(rt, "GPU 序号", _numGpu, "检测间隔 (秒)", _numInterval);
        AddPair(rt, "降温温度 (°C) >", _numTarget, "恢复温度 (°C) ≤", _numCool);
        AddPair(rt, "紧急温度 (°C) ≥", _numCritical, "", null);
        AddPair(rt, "频率上限 (MHz)", _numCeiling, "频率下限 (MHz)", _numFloor);
        AddPair(rt, "降频步进 (MHz)", _numStepDown, "升频步进 (MHz)", _numStepUp);
        AddPair(rt, "锁频最低 (MHz)", _numLockMin, "", null);
        AddPair(rt, "功耗上限 (W)", _numPower, "功耗下限 (W)", _numPowerFloor);
        AddPair(rt, "降功耗步进 (W)", _numPowerStepDown, "升功耗步进 (W)", _numPowerStepUp);
        foreach (var n in new[] { _numTarget, _numCool, _numCritical })
            n.ValueChanged += (_, _) => { if (!_loadingUi) SetProfileUi(ReadUiConfig().DetectProfile()); };
        var help = new Label
        {
            AutoSize = true, MaximumSize = new Size(410, 0), ForeColor = Color.DimGray,
            Text = "勾选「控制全部 GPU」时，每块卡按自己的温度独立调整，共用下面这套规则。取消后只控制「GPU 序号」那一块。" +
                   "逻辑：温度 > 降温温度 → 每次检测把限制下调一个步进；≥ 紧急温度 → 下调 3 倍步进；≤ 恢复温度 → 上调一个步进，直至上限；永远不低于下限。" +
                   "锁频模式用频率参数（上限自动钳到显卡最高频率，锁频最低自动钳到显卡支持的最低频率）；" +
                   "限功耗模式用功耗参数（0 = 使用显卡默认/最低功耗限制）。GeForce 卡（如 RTX 3090）在 Windows 下通常不支持锁频，「自动」会改用限功耗。" +
                   "锁频模式下功耗上限 > 0 时作为一次性安全上限。",
            Padding = new Padding(0, 6, 0, 6),
        };
        rt.Controls.Add(help); rt.SetColumnSpan(help, 4);

        var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var btnSave = new Button { Text = "保存并应用", AutoSize = true };
        var btnReset = new Button { Text = "恢复默认", AutoSize = true };
        btnSave.Click += (_, _) => SaveRules();
        btnReset.Click += (_, _) => { LoadConfigToUi(new Config { AutoCoolEnabled = _chkAuto.Checked }); SaveRules(); };
        btns.Controls.Add(btnSave); btns.Controls.Add(btnReset);
        rt.Controls.Add(btns); rt.SetColumnSpan(btns, 4);
        rules.Controls.Add(rt);
        root.Controls.Add(rules);

        var cfgPath = new Label { AutoSize = true, ForeColor = Color.Gray, Text = "配置文件: " + Config.FilePath, MaximumSize = new Size(420, 0), Padding = new Padding(0, 8, 0, 0) };
        root.Controls.Add(cfgPath);
    }

    private static void AddPair(TableLayoutPanel t, string c1, NumericUpDown n1, string c2, NumericUpDown? n2)
    {
        t.Controls.Add(new Label { Text = c1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
        t.Controls.Add(n1);
        t.Controls.Add(new Label { Text = c2, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
        if (n2 != null) t.Controls.Add(n2); else t.Controls.Add(new Label());
    }

    // ---------- config <-> UI ----------

    private void LoadConfigToUi(Config c)
    {
        _loadingUi = true;
        _chkAll.Checked = c.ControlAllGpus;
        _numGpu.Enabled = !c.ControlAllGpus;
        _numGpu.Value = c.GpuIndex; _numTarget.Value = c.TargetTempC; _numCool.Value = c.CoolTempC;
        _numCritical.Value = c.CriticalTempC; _numInterval.Value = c.CheckIntervalSec;
        _numCeiling.Value = c.ClockCeilingMHz; _numFloor.Value = c.ClockFloorMHz; _numLockMin.Value = c.ClockLockMinMHz;
        _numStepDown.Value = c.StepDownMHz; _numStepUp.Value = c.StepUpMHz; _numPower.Value = c.PowerLimitW;
        _numPowerFloor.Value = c.PowerFloorW; _numPowerStepDown.Value = Math.Max(1, c.PowerStepDownW); _numPowerStepUp.Value = Math.Max(1, c.PowerStepUpW);
        var mi = Array.FindIndex(Config.ControlModes, m => m.Mode == c.ControlMode);
        _cmbMode.SelectedIndex = mi >= 0 ? mi : 0;
        _chkAuto.Checked = c.AutoCoolEnabled; _menuAuto.Checked = c.AutoCoolEnabled;
        SetProfileUi(c.DetectProfile());
        try { _chkAutostart.Checked = Autostart.IsEnabled(); } catch { }
        _loadingUi = false;
    }

    private Config ReadUiConfig() => new()
    {
        ControlAllGpus = _chkAll.Checked,
        GpuIndex = (int)_numGpu.Value, TargetTempC = (int)_numTarget.Value, CoolTempC = (int)_numCool.Value,
        CriticalTempC = (int)_numCritical.Value, CheckIntervalSec = (int)_numInterval.Value,
        ClockCeilingMHz = (int)_numCeiling.Value, ClockFloorMHz = (int)_numFloor.Value, ClockLockMinMHz = (int)_numLockMin.Value,
        StepDownMHz = (int)_numStepDown.Value, StepUpMHz = (int)_numStepUp.Value, PowerLimitW = (int)_numPower.Value,
        PowerFloorW = (int)_numPowerFloor.Value, PowerStepDownW = (int)_numPowerStepDown.Value, PowerStepUpW = (int)_numPowerStepUp.Value,
        ControlMode = _cmbMode.SelectedIndex >= 0 ? Config.ControlModes[_cmbMode.SelectedIndex].Mode : ControlMode.Auto,
        AutoCoolEnabled = _chkAuto.Checked,
        Profile = _cmbProfile.SelectedIndex >= 0 && _cmbProfile.SelectedIndex < Config.Presets.Length ? Config.Presets[_cmbProfile.SelectedIndex].Key : "custom",
    };

    private void SetProfileUi(string key)
    {
        var idx = Array.FindIndex(Config.Presets, p => p.Key == key);
        _cmbProfile.SelectedIndex = idx >= 0 ? idx : Config.Presets.Length;
        foreach (ToolStripMenuItem mi in _menuProfile.DropDownItems) mi.Checked = (string?)mi.Tag == key;
    }

    /// <summary>Preset chosen: fill the temperature fields, save and apply immediately.</summary>
    private void OnProfileChanged()
    {
        var idx = _cmbProfile.SelectedIndex;
        if (idx < 0 || idx >= Config.Presets.Length) { SetProfileUi("custom"); return; }
        var c = ReadUiConfig();
        c.ApplyPreset(Config.Presets[idx].Key);
        _loadingUi = true;
        _numTarget.Value = c.TargetTempC; _numCool.Value = c.CoolTempC; _numCritical.Value = c.CriticalTempC;
        _loadingUi = false;
        SetProfileUi(c.Profile);
        if (c.Validate() is string err) { MessageBox.Show(this, err, "规则无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        c.Save();
        _engine.ApplyConfig(c);
        RefreshAll();
    }

    private void SaveRules()
    {
        var c = ReadUiConfig();
        if (c.Validate() is string err) { MessageBox.Show(this, err, "规则无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        c.Profile = c.DetectProfile();
        c.Save();
        _engine.ApplyConfig(c);
        SetProfileUi(c.Profile);
        RefreshAll();
    }

    private void OnAllGpusToggled()
    {
        _numGpu.Enabled = !_chkAll.Checked;
        SaveRules();
    }

    private void OnAutoToggled()
    {
        var c = _engine.Config.Clone();
        c.AutoCoolEnabled = _chkAuto.Checked;
        _menuAuto.Checked = c.AutoCoolEnabled;
        c.Save();
        _engine.ApplyConfig(c);
    }

    private void OnAutostartToggled()
    {
        try { if (_chkAutostart.Checked) Autostart.Enable(); else Autostart.Disable(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "开机启动设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _loadingUi = true; _chkAutostart.Checked = Autostart.IsEnabled(); _loadingUi = false;
        }
    }

    // ---------- refresh ----------

    private void RefreshAll()
    {
        var cfg = _engine.Config;
        var reports = _engine.Reports;
        var seen = new HashSet<int>();
        foreach (var r in reports)
        {
            seen.Add(r.Index);
            if (!_cards.TryGetValue(r.Index, out var ui))
            {
                ui = GpuCardUi.Create();
                _cards[r.Index] = ui;
                var errorAt = _liveHost.Controls.IndexOf(_lblError);
                _liveHost.Controls.Add(ui.Panel);
                _liveHost.Controls.SetChildIndex(ui.Panel, errorAt < 0 ? _liveHost.Controls.Count - 1 : errorAt);
            }
            ui.Update(r, cfg);
        }
        foreach (var index in _cards.Keys.Where(i => !seen.Contains(i)).ToList())
        {
            _liveHost.Controls.Remove(_cards[index].Panel);
            _cards[index].Panel.Dispose();
            _cards.Remove(index);
        }
        _lblPlaceholder.Visible = reports.Count == 0;
        _lblPlaceholder.Text = _engine.LastError ?? "正在读取显卡…";
        _lblError.Text = reports.Count == 0 ? "" : _engine.LastError ?? "";
        if (_menuAuto.Checked != cfg.AutoCoolEnabled) _menuAuto.Checked = cfg.AutoCoolEnabled;
        RefreshTray();
    }

    private void RefreshTray()
    {
        var reports = _engine.Reports;
        var cfg = _engine.Config;
        var known = reports.Where(r => r.State != null).ToList();
        int? temp = known.Count == 0 ? null : known.Max(r => r.State!.TempC);
        var throttling = reports.Any(r => r.IsThrottling);
        // Cached samples must not hide a later query failure or a per-GPU control error.
        var error = _engine.LastError != null || reports.Any(r => r.LastError != null);
        var icon = TrayIconRenderer.Render(temp, throttling, cfg.AutoCoolEnabled, error, cfg.TargetTempC, cfg.CriticalTempC);
        var old = _trayIcon;
        _tray.Icon = icon; _trayIcon = icon;
        old?.Dispose();
        string tip;
        if (known.Count == 0) tip = error ? "GPU Guard 异常" : "GPU Guard";
        else
        {
            var head = string.Join(" ", known.Select(r => $"#{r.Index} {r.State!.TempC}°"));
            var tail = error ? "异常" : !cfg.AutoCoolEnabled ? "关" : throttling ? "降温中" : "开";
            tip = $"{head} {tail} ≤{cfg.TargetTempC}°";
        }
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private static string ActionText(string action, bool power) => action switch
    {
        "drop" => power ? "降功耗中" : "降频中",
        "critical-drop" => power ? "紧急降功耗中" : "紧急降频中",
        "raise" => power ? "升功耗中" : "升频中",
        "hold" => "保持",
        "clamp" => "钳位到范围内",
        "off" => "自动降温已关闭",
        _ => action,
    };

    /// <summary>One GPU's live block inside the status panel.</summary>
    private sealed class GpuCardUi
    {
        public Panel Panel { get; }
        private readonly Label _name = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
        private readonly Label _temp = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold) };
        private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
        private readonly Label _mode = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
        private readonly Label _cap = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
        private readonly Label _action = new() { AutoSize = true };
        private readonly Label _notice = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DarkOrange };
        private readonly Label _error = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.Firebrick };

        private GpuCardUi(Panel panel) { Panel = panel; }

        public static GpuCardUi Create()
        {
            var ui = new GpuCardUi(new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 10), MinimumSize = new Size(420, 0),
            });
            var host = (FlowLayoutPanel)ui.Panel;
            host.Controls.Add(ui._name);
            host.Controls.Add(ui._temp);
            host.Controls.Add(ui._detail);
            host.Controls.Add(ui._mode);
            host.Controls.Add(ui._cap);
            host.Controls.Add(ui._action);
            host.Controls.Add(ui._notice);
            host.Controls.Add(ui._error);
            return ui;
        }

        public void Update(GpuReport r, Config cfg)
        {
            var s = r.State;
            _name.Text = s == null ? $"GPU #{r.Index}" : $"GPU #{s.Index}  {s.Name}";
            if (s == null)
            {
                _temp.Text = "--";
                _temp.ForeColor = Color.DimGray;
                _detail.Text = "";
            }
            else
            {
                _temp.Text = $"{s.TempC} °C";
                _temp.ForeColor = s.TempC >= cfg.CriticalTempC ? Color.Firebrick : s.TempC > cfg.TargetTempC ? Color.DarkOrange : Color.ForestGreen;
                _detail.Text = $"{s.ClockSmMHz} MHz · {s.PowerDrawW:N1} W（限制 {s.MinLimitW}–{s.MaxLimitW}）· 风扇 {s.FanPct}% · 占用 {s.UtilPct}% · 显存 {s.MemUsedMiB}/{s.MemTotalMiB} MiB";
            }
            _mode.Text = "控制方式  " + r.ModeText;
            _cap.Text = "当前限制  " + r.CapText;
            _action.Text = "当前动作  " + ActionText(r.LastAction, r.Active == ActiveMode.Power);
            _notice.Text = r.Notice ?? "";
            _error.Text = r.LastError ?? "";
        }
    }

    // ---------- window behaviour ----------

    private void TogglePanel() { if (Visible && WindowState != FormWindowState.Minimized) Hide(); else ShowPanel(); }

    private void ShowPanel()
    {
        PositionNearTray();
        Show(); WindowState = FormWindowState.Normal; Activate();
        RefreshAll();
    }

    private void PositionNearTray()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(wa.Right - Width - 8, wa.Bottom - Height - 8);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Popup-style: hide when focus leaves, like a tray flyout.
        if (Visible) BeginInvoke(() => { if (Form.ActiveForm != this) Hide(); });
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _engine.Dispose();
        _tray.Dispose();
        Application.Exit();
    }
}
