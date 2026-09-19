using System.Diagnostics;

namespace MuMuAdBlocker;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private AdbRunner? _adb;
    private MuMuManager? _manager;
    private List<MuMuDevice> _devices = new();
    private bool _busy;

    // 컨트롤
    private TextBox _txtAdbPath = null!;
    private ComboBox _cmbDevices = null!;
    private TextBox _txtEndpoint = null!;
    private Label _lblAdbStatus = null!, _lblStore = null!, _lblVersion = null!, _lblAd = null!;
    private Button _btnAuto = null!, _btnBrowse = null!, _btnTest = null!;
    private Button _btnRefresh = null!, _btnConnect = null!;
    private Button _btnBlock = null!, _btnRestore = null!, _btnApplyAll = null!, _btnDiag = null!;
    private TextBox _txtLog = null!;
    private Button _btnGuard = null!, _btnUnGuard = null!, _btnOriginal = null!;
    private Label _lblGuard = null!;

    public MainForm()
    {
        _settings = SettingsStore.Load();
        InitializeUi();
        Shown += async (_, _) => await StartupAsync();
        FormClosing += (_, _) => SettingsStore.Save(_settings);
    }

    private void InitializeUi()
    {
        Text = "MuMuPlayer 광고 오버레이 차단 · 1.1";
        Font = new Font("맑은 고딕", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 700);
        Size = new Size(760, 820);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── ADB 영역 ──
        var adbGroup = new GroupBox { Text = "ADB", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        var adbLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4) };
        adbLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        adbLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _txtAdbPath = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var btnRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        for (int i = 0; i < 3; i++) btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _btnAuto = new Button { Text = "자동 찾기", AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(2, 0, 2, 0) };
        _btnBrowse = new Button { Text = "찾아보기...", AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(2, 0, 2, 0) };
        _btnTest = new Button { Text = "ADB 테스트", AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(2, 0, 2, 0) };
        btnRow.Controls.Add(_btnAuto, 0, 0);
        btnRow.Controls.Add(_btnBrowse, 1, 0);
        btnRow.Controls.Add(_btnTest, 2, 0);
        adbLayout.Controls.Add(_txtAdbPath, 0, 0);
        adbLayout.Controls.Add(btnRow, 1, 0);
        adbGroup.Controls.Add(adbLayout);

        // ── 장치 영역 ──
        var devGroup = new GroupBox { Text = "MuMu 인스턴스", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        var devLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), Margin = new Padding(0) };
        devLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        devLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        devLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _cmbDevices = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _btnRefresh = new Button { Text = "새로고침", AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(2, 0, 2, 0) };
        _btnApplyAll = new Button { Text = "모든 인스턴스에 적용", AutoSize = true, MinimumSize = new Size(140, 30), Margin = new Padding(2, 0, 2, 0) };
        devLayout.Controls.Add(_cmbDevices, 0, 0);
        devLayout.Controls.Add(_btnRefresh, 1, 0);
        devLayout.Controls.Add(_btnApplyAll, 2, 0);

        var manualLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), Margin = new Padding(0) };
        manualLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        manualLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        manualLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        manualLayout.Controls.Add(new Label { Text = "수동 연결:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 0, 8, 0) }, 0, 0);
        _txtEndpoint = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "127.0.0.1:포트 (MuMu 설정에서 확인)", Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _btnConnect = new Button { Text = "연결", AutoSize = true, MinimumSize = new Size(80, 30), Margin = new Padding(2, 0, 2, 0) };
        manualLayout.Controls.Add(_txtEndpoint, 1, 0);
        manualLayout.Controls.Add(_btnConnect, 2, 0);
        var devStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        devStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devStack.Controls.Add(devLayout, 0, 0);
        devStack.Controls.Add(manualLayout, 0, 1);
        devGroup.Controls.Add(devStack);

        // ── 상태 영역 ──
        var statusGroup = new GroupBox { Text = "상태", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        var statusGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), Margin = new Padding(0) };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _lblAdbStatus = MakeStatusRow(statusGrid, 0, "ADB");
        _lblStore = MakeStatusRow(statusGrid, 1, "MuMu Store");
        _lblVersion = MakeStatusRow(statusGrid, 2, "Store 버전");
        _lblAd = MakeStatusRow(statusGrid, 3, "Store 오버레이 권한");
        _lblGuard = MakeStatusRow(statusGrid, 4, "자동 유지");
        _lblGuard.MaximumSize = new Size(530, 0);
        statusGroup.Controls.Add(statusGrid);

        // ── 버튼 영역 ──
        var actionPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(2) };
        for (int i = 0; i < 3; i++) actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _btnBlock = new Button { Text = "중앙 광고 제거", AutoSize = true, MinimumSize = new Size(170, 40), BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 2, 8, 2), UseVisualStyleBackColor = false };
        _btnRestore = new Button { Text = "기본값으로 복원", AutoSize = true, MinimumSize = new Size(170, 40), Margin = new Padding(2, 2, 8, 2) };
        _btnDiag = new Button { Text = "진단 정보 복사", AutoSize = true, MinimumSize = new Size(120, 40), Margin = new Padding(2, 2, 2, 2) };
        actionPanel.Controls.Add(_btnBlock, 0, 0);
        actionPanel.Controls.Add(_btnRestore, 1, 0);
        actionPanel.Controls.Add(_btnDiag, 2, 0);
        _btnGuard = new Button { Text = "자동 유지 설치·갱신", AutoSize = true, MinimumSize = new Size(170, 36), Margin = new Padding(2, 8, 8, 2) };
        _btnUnGuard = new Button { Text = "자동 유지 해제", AutoSize = true, MinimumSize = new Size(170, 36), Margin = new Padding(2, 8, 8, 2) };
        _btnOriginal = new Button { Text = "원래 권한 복원", AutoSize = true, MinimumSize = new Size(120, 36), Margin = new Padding(2, 8, 2, 2) };
        actionPanel.Controls.Add(_btnGuard, 0, 1);
        actionPanel.Controls.Add(_btnUnGuard, 1, 1);
        actionPanel.Controls.Add(_btnOriginal, 2, 1);
        var note = new Label { AutoSize = true, MaximumSize = new Size(690, 0), Text = "자동 유지: 한 번 설치 후 창을 닫아도 로그인 중 1분마다 짧게 점검합니다. MuMu·게임을 자동 실행/종료하지 않습니다.", Margin = new Padding(3, 6, 3, 6) };
        actionPanel.Controls.Add(note, 0, 2); actionPanel.SetColumnSpan(note, 3);

        // ── 로그 영역 ──
        var logGroup = new GroupBox { Text = "로그", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        _txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        logGroup.Controls.Add(_txtLog);

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(adbGroup, 0, 0);
        root.Controls.Add(devGroup, 0, 1);
        root.Controls.Add(statusGroup, 0, 2);
        root.Controls.Add(actionPanel, 0, 3);
        root.Controls.Add(logGroup, 0, 4);
        Controls.Add(root);

        // 이벤트
        _btnAuto.Click += async (_, _) => await AutoFindAsync();
        _btnBrowse.Click += async (_, _) => await BrowseAsync();
        _btnTest.Click += async (_, _) => await TestAdbAsync();
        _btnRefresh.Click += async (_, _) => await RefreshDevicesAsync();
        _btnConnect.Click += async (_, _) => await ConnectEndpointAsync();
        _btnBlock.Click += async (_, _) => await ApplyBlockAsync();
        _btnRestore.Click += async (_, _) => await ApplyRestoreAsync();
        _btnApplyAll.Click += async (_, _) => await ApplyAllAsync();
        _btnDiag.Click += async (_, _) => await CopyDiagnosticsAsync();
        _btnGuard.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            GuardTask.Install(_settings);
            await GuardHost.RunOnceAsync();
            UpdateGuardStatus();
            Log("자동 유지 설치/갱신 완료. 최초 점검 결과: " + new GuardStore().StatusSummary());
        });
        _btnUnGuard.Click += async (_, _) => await RunBusyAsync(() =>
        {
            GuardTask.Disable(); UpdateGuardStatus(); Log("자동 유지 해제. 기존 차단 권한은 유지합니다.");
            return Task.CompletedTask;
        });
        _btnOriginal.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            GuardTask.Disable();
            var result = await GuardHost.RestoreSavedAsync();
            Log(result); UpdateGuardStatus();
            await RefreshDevicesCoreAsync();
            MessageBox.Show(this, result, "원래 권한 복원", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        _cmbDevices.SelectedIndexChanged += async (_, _) => await UpdateSelectedStatusAsync();

        SetBusy(false);
    }

    private static Label MakeStatusRow(TableLayoutPanel grid, int row, string caption)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 6, 12, 3), Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) }, 0, row);
        var value = new Label { Text = "-", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        grid.Controls.Add(value, 1, row);
        return value;
    }

    public override Size GetPreferredSize(Size proposedSize) => MinimumSize;

    // ─────────────────────────────────────────────

    private void Log(string message)
    {
        if (_txtLog.InvokeRequired) { _txtLog.BeginInvoke(() => Log(message)); return; }
        _txtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        try
        {
            Directory.CreateDirectory(SettingsStore.LogDirectory);
            File.AppendAllText(Path.Combine(SettingsStore.LogDirectory,
                $"log_{DateTime.Now:yyyyMMdd}.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var b in new[] { _btnAuto, _btnBrowse, _btnTest, _btnRefresh, _btnConnect, _btnBlock, _btnRestore, _btnApplyAll, _btnDiag, _btnGuard, _btnUnGuard, _btnOriginal })
            b.Enabled = !busy;
        _cmbDevices.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (_busy) return;
        SetBusy(true);
        try { await work(); }
        catch (TimeoutException ex) { ShowError("ADB 응답 시간 초과", ex.Message); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("오류", ex.Message); }
        finally { SetBusy(false); }
    }

    private void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private async Task StartupAsync()
    {
        Log("프로그램 시작");
        _txtEndpoint.Text = _settings.LastEndpoint;
        UpdateGuardStatus();
        if (!string.IsNullOrWhiteSpace(_settings.AdbPath))
        {
            _txtAdbPath.Text = _settings.AdbPath;
            if (await AdbLocator.IsValidAsync(_settings.AdbPath))
            {
                _adb = new AdbRunner(_settings.AdbPath);
                _manager = new MuMuManager(_adb, Log);
                Log($"저장된 ADB 사용: {_settings.AdbPath}");
                await RefreshDevicesAsync();
                return;
            }
            Log("저장된 ADB 경로를 사용할 수 없습니다. 다시 탐색합니다.");
        }
        await AutoFindAsync();
    }

    private Task AutoFindAsync() => RunBusyAsync(async () =>
    {
        Log("ADB 자동 탐색 중...");
        var found = await AdbLocator.FindAsync(_settings.AdbPath);
        if (found is null)
        {
            Log("ADB를 자동으로 찾지 못했습니다.");
            var r = MessageBox.Show(this,
                "ADB를 자동으로 찾지 못했습니다.\nadb.exe를 직접 선택하십시오.",
                "ADB 없음", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r == DialogResult.OK) { await BrowseCoreAsync(); return; }
            return;
        }
        await UseAdbAsync(found);
    });

    private Task BrowseAsync() => RunBusyAsync(BrowseCoreAsync);

    private async Task BrowseCoreAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "adb.exe 선택",
            Filter = "adb.exe|adb.exe|실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(_txtAdbPath.Text)))
            dlg.InitialDirectory = Path.GetDirectoryName(_txtAdbPath.Text);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await UseAdbAsync(dlg.FileName);
    }

    private async Task UseAdbAsync(string path)
    {
        Log($"ADB 검증 중: {path}");
        var (ok, msg) = await AdbRunner.ValidateAsync(path);
        if (!ok)
        {
            Log("ADB 검증 실패");
            ShowError("ADB 실행 불가", msg + "\n\n선택한 ADB를 실행할 수 없습니다.\nPlatform Tools 구성이 정상인지 확인하십시오.");
            return;
        }
        _adb = new AdbRunner(path);
        _manager = new MuMuManager(_adb, Log);
        _txtAdbPath.Text = path;
        _settings.AdbPath = path;
        SettingsStore.Save(_settings);
        Log("adb version 성공 — ADB 준비 완료");
        await RefreshDevicesCoreAsync();
    }

    private Task TestAdbAsync() => RunBusyAsync(async () =>
    {
        if (_adb is null) { ShowError("ADB 없음", "먼저 adb.exe를 찾거나 선택하십시오."); return; }
        var (ok, msg) = await AdbRunner.ValidateAsync(_adb.AdbPath);
        if (ok) { Log("adb version 성공"); MessageBox.Show(this, "ADB 정상 작동:\n" + msg.Split('\n')[0], "ADB 테스트", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        else { Log("ADB 테스트 실패"); ShowError("ADB 실행 불가", msg); }
    });

    private Task RefreshDevicesAsync() => RunBusyAsync(RefreshDevicesCoreAsync);

    private async Task RefreshDevicesCoreAsync()
    {
        if (_manager is null) { ShowError("ADB 없음", "먼저 adb.exe를 찾거나 선택하십시오."); return; }
        Log("장치 검색 중...");
        UpdateGuardStatus();
        if (_adb is not null)
            await EndpointDiscovery.ConnectAsync(_adb,
                await EndpointDiscovery.DiscoverAsync(_adb.AdbPath, new[] { _settings.LastEndpoint }), CancellationToken.None);
        _devices = await _manager.FindMuMuDevicesAsync();
        _cmbDevices.Items.Clear();
        foreach (var d in _devices)
            _cmbDevices.Items.Add($"{d.Serial}   ({MuMuManager.Describe(d.OpState)})");
        if (_devices.Count == 0)
        {
            Log("연결된 MuMuPlayer 인스턴스가 없습니다.");
            SetStatus(_lblAdbStatus, "연결됨", Color.Green);
            SetStatus(_lblStore, "발견 안 됨", Color.Gray);
            SetStatus(_lblVersion, "-", Color.Black);
            SetStatus(_lblAd, "-", Color.Gray);
            MessageBox.Show(this,
                "연결된 MuMuPlayer 인스턴스를 찾지 못했습니다.\n" +
                "MuMu 설정에서 ADB 디버깅이 켜져 있는지 확인하고,\n아래 '수동 연결'에 포트를 입력해 연결할 수 있습니다.",
                "MuMu 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Log($"MuMu device 발견: {string.Join(", ", _devices.Select(d => d.Serial))}");

        // 저장된 마지막 장치 우선 선택
        var idx = _devices.FindIndex(d => d.Serial == _settings.LastDevice);
        _cmbDevices.SelectedIndex = idx >= 0 ? idx : 0;
        await UpdateSelectedStatusCoreAsync();
    }

    private Task ConnectEndpointAsync() => RunBusyAsync(async () =>
    {
        if (_adb is null) { ShowError("ADB 없음", "먼저 adb.exe를 찾거나 선택하십시오."); return; }
        var ep = EndpointDiscovery.Normalize(_txtEndpoint.Text);
        if (ep is null)
        {
            ShowError("입력 필요", "로컬 MuMu 주소를 입력하십시오. 예) 127.0.0.1:16384 (USB/원격 장치는 변경하지 않습니다)");
            return;
        }
        Log($"adb connect {ep}");
        var r = await _adb.RunAsync(new[] { "connect", ep });
        Log(r.Combined.Trim());
        if (!r.Success || r.StdOut.Contains("cannot", StringComparison.OrdinalIgnoreCase) || r.StdOut.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("연결 실패", $"연결에 실패했습니다.\n{r.Combined.Trim()}\n\nMuMu 설정 → 기타에서 ADB 포트를 확인하십시오.");
            return;
        }
        _settings.LastEndpoint = ep;
        SettingsStore.Save(_settings);
        await RefreshDevicesCoreAsync();
    });

    private MuMuDevice? SelectedDevice =>
        _cmbDevices.SelectedIndex >= 0 && _cmbDevices.SelectedIndex < _devices.Count
            ? _devices[_cmbDevices.SelectedIndex] : null;

    private Task UpdateSelectedStatusAsync() => RunBusyAsync(UpdateSelectedStatusCoreAsync);

    private async Task UpdateSelectedStatusCoreAsync()
    {
        var dev = SelectedDevice;
        if (dev is null || _manager is null) return;
        _settings.LastDevice = dev.Serial;
        SettingsStore.Save(_settings);
        Log($"선택: {dev.Serial}");
        // 최신 상태 재조회
        var fresh = await _manager.InspectAsync(dev.Serial);
        if (fresh is null)
        {
            SetStatus(_lblStore, "발견 안 됨", Color.Red);
            SetStatus(_lblVersion, "-", Color.Black);
            SetStatus(_lblAd, "-", Color.Gray);
            ShowError("Store 없음", $"com.mumu.store 패키지를 찾지 못했습니다.\n지원하지 않는 MuMu 버전일 수 있습니다.\n아무 설정도 변경하지 않았습니다.");
            return;
        }
        _devices[_cmbDevices.SelectedIndex] = fresh;
        SetStatus(_lblAdbStatus, "연결됨", Color.Green);
        SetStatus(_lblStore, "발견됨", Color.Green);
        SetStatus(_lblVersion, fresh.VersionName, Color.Black);
        SetStatus(_lblAd, MuMuManager.Describe(fresh.OpState),
            fresh.OpState == AppOpState.Ignore ? Color.Green :
            fresh.OpState == AppOpState.Default || fresh.OpState == AppOpState.Allow ? Color.Firebrick : Color.Gray);
        Log($"현재 SYSTEM_ALERT_WINDOW: {fresh.OpState}");
    }

    private Task ApplyBlockAsync() => RunBusyAsync(async () =>
    {
        var dev = SelectedDevice;
        if (dev is null || _manager is null) { ShowError("MuMu 없음", "대상 MuMu 인스턴스를 먼저 선택하십시오."); return; }

        var (state, _) = await _manager.GetAppOpStateAsync(dev.Serial);
        if (state == AppOpState.Ignore)
        {
            Log("이미 광고 차단 상태입니다.");
            SetStatus(_lblAd, MuMuManager.Describe(state), Color.Green);
            MessageBox.Show(this,
                "Store 오버레이 권한은 이미 차단되어 있습니다.\n광고가 계속 보이면 다른 표시 방식일 수 있습니다. 진단 정보를 확인하십시오.",
                "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var (ok, final, msg) = await _manager.SetAppOpAsync(dev.Serial, "ignore");
        Log(msg.Replace('\n', ' '));
        if (!ok) { ShowError("적용 실패", msg); await UpdateSelectedStatusCoreAsync(); return; }

        SetStatus(_lblAd, MuMuManager.Describe(final), Color.Green);
        MessageBox.Show(this,
            "Store 오버레이 권한 차단이 검증되었습니다. 모든 광고의 제거를 뜻하지는 않습니다.\n\n" +
            "이미 화면에 떠 있는 광고는 X 버튼으로 한 번 닫으십시오.\n" +
            "필요하면 MuMuPlayer를 재시작하십시오.",
            "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private Task ApplyRestoreAsync() => RunBusyAsync(async () =>
    {
        var dev = SelectedDevice;
        if (dev is null || _manager is null) { ShowError("MuMu 없음", "대상 MuMu 인스턴스를 먼저 선택하십시오."); return; }

        if (new GuardStore().Load().Enabled)
        {
            GuardTask.Disable(); UpdateGuardStatus();
            Log("복원한 권한이 다시 차단되지 않도록 자동 유지를 해제했습니다.");
        }
        var (ok, final, msg) = await _manager.SetAppOpAsync(dev.Serial, "default");
        Log(msg.Replace('\n', ' '));
        if (!ok) { ShowError("복원 실패", msg); await UpdateSelectedStatusCoreAsync(); return; }
        SetStatus(_lblAd, MuMuManager.Describe(final), Color.Firebrick);
        MessageBox.Show(this, "기본값으로 복원되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private Task ApplyAllAsync() => RunBusyAsync(async () =>
    {
        if (_manager is null || _devices.Count == 0) { ShowError("MuMu 없음", "적용할 MuMu 인스턴스가 없습니다."); return; }
        var r = MessageBox.Show(this,
            $"발견된 모든 MuMu 인스턴스({_devices.Count}개)에 광고 제거를 적용하시겠습니까?",
            "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        var failures = new List<string>();
        foreach (var dev in _devices.ToList())
        {
            var (ok, _, msg) = await _manager.SetAppOpAsync(dev.Serial, "ignore");
            Log($"[{dev.Serial}] {msg.Replace('\n', ' ')}");
            if (!ok) failures.Add($"{dev.Serial}: {msg}");
        }
        await RefreshDevicesCoreAsync();
        if (failures.Count > 0)
            ShowError("일부 실패", string.Join("\n\n", failures));
        else
            MessageBox.Show(this, "모든 인스턴스에 적용되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private Task CopyDiagnosticsAsync() => RunBusyAsync(async () =>
    {
        var sb = new System.Text.StringBuilder();
        var guardStore = new GuardStore();
        sb.AppendLine("자동 유지: " + guardStore.StatusSummary());
        sb.AppendLine("예약 작업: " + (GuardTask.ReadXml(GuardTask.TaskName) is null ? "없음" : "등록됨"));
        sb.AppendLine($"프로그램 버전: {Application.ProductVersion}");
        sb.AppendLine($"ADB: {(string.IsNullOrWhiteSpace(_txtAdbPath.Text) ? "(없음)" : Path.GetFileName(_txtAdbPath.Text))}");
        if (_adb is not null)
        {
            try
            {
                var v = await _adb.RunAsync(new[] { "version" }, TimeSpan.FromSeconds(8));
                var line = v.StdOut.Split('\n').FirstOrDefault() ?? "";
                sb.AppendLine($"adb version: {line.Trim()}");
            }
            catch { sb.AppendLine("adb version: 확인 실패"); }
        }
        var dev = SelectedDevice;
        if (dev is not null)
        {
            sb.AppendLine($"Device: {dev.Serial}");
            sb.AppendLine($"Store 버전: {dev.VersionName} ({dev.VersionCode})");
            sb.AppendLine($"AppOps 상태: {MuMuManager.Describe(dev.OpState)}");
            if (_manager is not null)
            {
                var (current, raw) = await _manager.GetAppOpStateAsync(dev.Serial);
                sb.AppendLine($"실시간 권한: {current} / {raw}");
                var home = await _manager.Shell(dev.Serial, "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME");
                sb.AppendLine("HOME: " + home.Combined.Trim());
                var windows = await _manager.Shell(dev.Serial, "dumpsys window windows");
                foreach (var line in windows.StdOut.Split('\n').Where(l => l.Contains("PopupAd", StringComparison.OrdinalIgnoreCase) || l.Contains("com.mumu.store", StringComparison.OrdinalIgnoreCase)).Take(25))
                    sb.AppendLine(line.Trim());
            }
        }
        else sb.AppendLine("Device: (없음)");
        try { Clipboard.SetText(sb.ToString()); Log("진단 정보를 클립보드에 복사했습니다."); }
        catch { ShowError("복사 실패", "클립보드 접근에 실패했습니다."); }
    });

    private void UpdateGuardStatus()
    {
        try
        {
            var store = new GuardStore();
            var enabled = store.Load().Enabled;
            var registered = GuardTask.ReadXml(GuardTask.TaskName) is not null;
            _lblGuard.Text = (enabled && registered ? "자동 점검 등록 · " : enabled ? "예약 작업 없음 / 재설치 필요 · " : "자동 유지 꺼짐 · ") + store.StatusSummary();
        }
        catch (Exception ex) { _lblGuard.Text = "자동 유지 상태 확인 실패: " + ex.Message; }
    }

    private static void SetStatus(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }
}
