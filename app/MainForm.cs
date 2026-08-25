namespace GpuGuard;

/// <summary>Tray-resident main window: live GPU data, auto-cool toggle, rule settings, autostart.</summary>
public sealed class MainForm : Form
{
    private readonly GuardEngine _engine;
    private readonly NotifyIcon _tray;
    private Icon? _trayIcon;

    // Live data labels
    private readonly Label _lblName = new();
    private readonly Label _lblTemp = new();
    private readonly Label _lblClock = new();
    private readonly Label _lblPower = new();
    private readonly Label _lblFan = new();
    private readonly Label _lblUtil = new();
    private readonly Label _lblMem = new();
    private readonly Label _lblCap = new();
    private readonly Label _lblAction = new();
    private readonly Label _lblError = new();

    private readonly CheckBox _chkAuto = new() { Text = "启用自动 GPU 降温（锁频）", AutoSize = true, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
    private readonly CheckBox _chkAutostart = new() { Text = "开机自动启动（计划任务，管理员权限）", AutoSize = true };
    private readonly ToolStripMenuItem _menuAuto = new("自动降温");
    private readonly ToolStripMenuItem _menuProfile = new("降温策略");
    private readonly ComboBox _cmbProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };

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
        ClientSize = new Size(480, 800);
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

        // Live data
        var live = new GroupBox { Text = "GPU 实时数据", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var lt = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        lt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        lt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(lt, "显卡", _lblName);
        AddRow(lt, "温度", _lblTemp);
        AddRow(lt, "核心频率", _lblClock);
        AddRow(lt, "功耗", _lblPower);
        AddRow(lt, "风扇", _lblFan);
        AddRow(lt, "占用率", _lblUtil);
        AddRow(lt, "显存", _lblMem);
        AddRow(lt, "当前锁频上限", _lblCap);
        AddRow(lt, "当前动作", _lblAction);
        _lblError.ForeColor = Color.Firebrick; _lblError.AutoSize = true; _lblError.MaximumSize = new Size(400, 0);
        lt.Controls.Add(_lblError); lt.SetColumnSpan(_lblError, 2);
        _lblTemp.Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold);
        live.Controls.Add(lt);
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
        AddPair(rt, "GPU 序号", _numGpu, "检测间隔 (秒)", _numInterval);
        AddPair(rt, "降温温度 (°C) >", _numTarget, "恢复温度 (°C) ≤", _numCool);
        AddPair(rt, "紧急温度 (°C) ≥", _numCritical, "功耗上限 (W)", _numPower);
        AddPair(rt, "频率上限 (MHz)", _numCeiling, "频率下限 (MHz)", _numFloor);
        AddPair(rt, "降频步进 (MHz)", _numStepDown, "升频步进 (MHz)", _numStepUp);
        AddPair(rt, "锁频最低 (MHz)", _numLockMin, "", null);
        foreach (var n in new[] { _numTarget, _numCool, _numCritical })
            n.ValueChanged += (_, _) => { if (!_loadingUi) SetProfileUi(ReadUiConfig().DetectProfile()); };
        var help = new Label
        {
            AutoSize = true, MaximumSize = new Size(410, 0), ForeColor = Color.DimGray,
            Text = "逻辑：功耗上限 0 = 不限制。温度 > 降温温度 → 每次检测把频率上限下调一个步进；≥ 紧急温度 → 下调 3 倍步进；" +
                   "≤ 恢复温度 → 上调一个步进，直至频率上限。频率永远不低于频率下限。",
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

    private static void AddRow(TableLayoutPanel t, string caption, Label value)
    {
        t.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4) });
        value.AutoSize = true; value.Anchor = AnchorStyles.Left; value.Margin = new Padding(0, 4, 0, 4);
        t.Controls.Add(value);
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
        _numGpu.Value = c.GpuIndex; _numTarget.Value = c.TargetTempC; _numCool.Value = c.CoolTempC;
        _numCritical.Value = c.CriticalTempC; _numInterval.Value = c.CheckIntervalSec;
        _numCeiling.Value = c.ClockCeilingMHz; _numFloor.Value = c.ClockFloorMHz; _numLockMin.Value = c.ClockLockMinMHz;
        _numStepDown.Value = c.StepDownMHz; _numStepUp.Value = c.StepUpMHz; _numPower.Value = c.PowerLimitW;
        _chkAuto.Checked = c.AutoCoolEnabled; _menuAuto.Checked = c.AutoCoolEnabled;
        SetProfileUi(c.DetectProfile());
        try { _chkAutostart.Checked = Autostart.IsEnabled(); } catch { }
        _loadingUi = false;
    }

    private Config ReadUiConfig() => new()
    {
        GpuIndex = (int)_numGpu.Value, TargetTempC = (int)_numTarget.Value, CoolTempC = (int)_numCool.Value,
        CriticalTempC = (int)_numCritical.Value, CheckIntervalSec = (int)_numInterval.Value,
        ClockCeilingMHz = (int)_numCeiling.Value, ClockFloorMHz = (int)_numFloor.Value, ClockLockMinMHz = (int)_numLockMin.Value,
        StepDownMHz = (int)_numStepDown.Value, StepUpMHz = (int)_numStepUp.Value, PowerLimitW = (int)_numPower.Value,
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
        var s = _engine.LastState;
        var cfg = _engine.Config;
        if (s != null)
        {
            _lblName.Text = $"#{s.Index}  {s.Name}";
            _lblTemp.Text = $"{s.TempC} °C";
            _lblTemp.ForeColor = s.TempC >= cfg.CriticalTempC ? Color.Firebrick : s.TempC > cfg.TargetTempC ? Color.DarkOrange : Color.ForestGreen;
            _lblClock.Text = $"{s.ClockSmMHz} MHz";
            _lblPower.Text = $"{s.PowerDrawW:N1} W  (限制范围 {s.MinLimitW}–{s.MaxLimitW} W)";
            _lblFan.Text = $"{s.FanPct} %";
            _lblUtil.Text = $"{s.UtilPct} %";
            _lblMem.Text = $"{s.MemUsedMiB} / {s.MemTotalMiB} MiB";
        }
        _lblCap.Text = _engine.CurrentCapMHz > 0 ? $"{_engine.CurrentCapMHz} MHz  (上限 {cfg.ClockCeilingMHz})" : "未锁频";
        _lblAction.Text = _engine.LastAction switch
        {
            "drop" => "降频中", "critical-drop" => "紧急降频中", "raise" => "升频中", "hold" => "保持", "off" => "自动降温已关闭", _ => _engine.LastAction,
        };
        _lblError.Text = _engine.LastError ?? "";
        if (_menuAuto.Checked != cfg.AutoCoolEnabled) _menuAuto.Checked = cfg.AutoCoolEnabled;
        RefreshTray();
    }

    private void RefreshTray()
    {
        var s = _engine.LastState;
        var cfg = _engine.Config;
        var icon = TrayIconRenderer.Render(s?.TempC, _engine.IsThrottling, cfg.AutoCoolEnabled, _engine.LastError != null, cfg.TargetTempC, cfg.CriticalTempC);
        var old = _trayIcon;
        _tray.Icon = icon; _trayIcon = icon;
        old?.Dispose();
        var tip = s == null ? "GPU Guard" :
            $"GPU {s.TempC}°C  {s.ClockSmMHz}MHz  {s.PowerDrawW:N0}W\n" +
            (cfg.AutoCoolEnabled ? (_engine.IsThrottling ? $"降温中 上限{_engine.CurrentCapMHz}MHz" : "自动降温：开") : "自动降温：关") +
            $" ≤{cfg.TargetTempC}°C";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
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
