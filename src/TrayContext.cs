namespace RmsLink;

public sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly Worker _worker;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private PreviewForm _preview;
    private readonly ReadingAlert _readingAlert=new();
    private readonly DateTime _startedAt=DateTime.Now;

    public TrayContext(AppConfig cfg, OcrService ocr, bool showPreview=false)
    {
        _cfg = cfg;
        _worker = new Worker(cfg, ocr);

        _statusItem = new ToolStripMenuItem("시작 중...") { Enabled = false };

        var previewItem = new ToolStripMenuItem("판독 미리보기 (정확도 확인)");
        previewItem.Click += (_, _) => ShowPreview();

        var reselectItem = new ToolStripMenuItem("로그 영역 다시 지정");
        reselectItem.Click += (_, _) => ReselectRegions();

        _autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행")
        {
            Checked = AutoStart.IsRegistered(), CheckOnClick = true
        };
        _autoStartItem.CheckedChanged += (_, _) =>
        {
            if (_autoStartItem.Checked) AutoStart.Register();
            else AutoStart.Unregister();
        };

        var logItem = new ToolStripMenuItem("로그 폴더 열기");
        logItem.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(AppConfig.Dir);
                System.Diagnostics.Process.Start("explorer.exe", AppConfig.Dir);
            }
            catch { }
        };

        var exportItem = new ToolStripMenuItem("진단 로그 바탕화면으로 내보내기");
        exportItem.Click += (_, _) =>
        {
            try
            {
                var zip = Diagnostics.ExportDiagnostics();
                MessageBox.Show("진단 파일을 바탕화면에 저장했습니다:\n\n" + zip +
                    "\n\n이 파일을 전달해 주시면 원격에서 원인을 확인할 수 있습니다.",
                    "RmsLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zip}\""); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("진단 파일 생성 실패: " + ex.Message, "RmsLink",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        var dashboard=new ToolStripMenuItem("RmsLink 대시보드 열기");
        dashboard.Click+=(_,_)=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cfg.ServerUrl+"/"){UseShellExecute=true});
        var choose=new ToolStripMenuItem("키텍 앱 선택 · 변경");
        choose.Click+=(_,_)=>{using var picker=new AppPickerForm();if(picker.ShowDialog()!=DialogResult.OK)return;cfg.SelectedApp=picker.Selection;cfg.Regions.Clear();cfg.RegionsRelative=true;cfg.Save();try{DesktopLinks.Ensure(cfg);}catch(Exception ex){Logger.Error(ex.Message);}Restart(true);};
        menu.Items.Add(dashboard);menu.Items.Add(choose);
        menu.Items.Add(previewItem);
        menu.Items.Add(reselectItem);
        menu.Items.Add(_autoStartItem);
        var restore=new ToolStripMenuItem("최소화된 키텍 창 자동 복원"){Checked=cfg.RestoreMinimized,CheckOnClick=true};
        restore.CheckedChanged+=(_,_)=>{cfg.RestoreMinimized=restore.Checked;cfg.Save();};menu.Items.Add(restore);
        menu.Items.Add(logItem);
        menu.Items.Add(exportItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Visible = true,
            Text = "RmsLink - RMS 화면 연동",
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowPreview();
        _icon.BalloonTipClicked += (_, _) => ShowPreview();
        _ = menu.Handle; // Create UI dispatch handle before worker callbacks.
        _worker.Sink.ExitForUpdate = () => { if(!menu.IsDisposed) menu.BeginInvoke(new Action(ExitApp)); };
        _worker.Start();
        try{DesktopLinks.Ensure(cfg);}catch(Exception ex){Logger.Error("바로가기: "+ex.Message);}
        if(cfg.SelectedApp==null)menu.BeginInvoke(new Action(()=>choose.PerformClick()));
        _icon.ShowBalloonTip(3000,"RmsLink 시작",$"호텔 [{cfg.HotelId}] · 관리 서버 연결 중",ToolTipIcon.Info);
        var updateItem=new ToolStripMenuItem("지금 업데이트 확인 · 적용");
        updateItem.Click+=(_,_)=>{_worker.Sink.ManualUpdateRequested=true;_icon.ShowBalloonTip(3000,"업데이트","관리 서버에서 최신 앱과 호텔 프로필을 확인합니다.",ToolTipIcon.Info);};
        menu.Items.Insert(2,updateItem);
        var settingsItem=new ToolStripMenuItem("호텔 ID 변경");
        settingsItem.Click+=(_,_)=>Restart(false);
        menu.Items.Insert(3,settingsItem);
        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
        if(showPreview)menu.BeginInvoke(new Action(ShowPreview));
    }

    private void UpdateStatus()
    {
        if(_worker.LastCycleAt!=DateTime.MinValue) File.WriteAllText(Path.Combine(AppConfig.InstallDir,"healthy-"+Updater.Version),DateTime.UtcNow.ToString("O"));
        var s = _worker.Sink;
        var health=_worker.Health;
        bool stalled=DateTime.Now-(_worker.LastCycleAt==DateTime.MinValue?_startedAt:_worker.LastCycleAt)>TimeSpan.FromSeconds(45);
        string reading=stalled?"화면 판독 응답이 멈췄습니다. RMS와 RmsLink를 다시 확인하세요":health.Message;
        string text =
            $"[{_cfg.HotelId}] 글자 {health.TextLines}줄 · 채택 {health.Accepted} · 전송 {s.SentCount} · 대기 {s.PendingCount}";
        _statusItem.Text = reading+" · "+text + (s.LastError.Length > 0 ? " · 전송 오류" : "") + " · " + s.UpdateStatus;
        if(_readingAlert.ShouldNotify(stalled || health.NeedsAttention || s.LastError.Length>0,DateTimeOffset.UtcNow))
            _icon.ShowBalloonTip(10000,"RmsLink · 연동 확인 필요",(s.LastError.Length>0?s.LastError:reading)+"\n알림을 누르면 판독 미리보기가 열립니다.",ToolTipIcon.Warning);
        string tip = "RmsLink - " + reading;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void ShowPreview()
    {
        if (_preview == null || _preview.IsDisposed)
        {
            _preview = new PreviewForm(_worker, _cfg);
            _preview.Show();
        }
        else
        {
            _preview.Activate();
        }
    }

    private void ReselectRegions()
    {
        var found=WindowProbe.Find(_cfg.SelectedApp);
        if(found.Count!=1||found[0].Minimized){MessageBox.Show("먼저 키텍 앱을 선택하고 창을 복원하세요.");return;}
        var w=found[0];WindowProbe.Activate(w);
        using var selector=new RoiSelectorForm();if(selector.ShowDialog()!=DialogResult.OK)return;
        var area=selector.SelectedScreenRect;
        if(!new Rectangle(w.X,w.Y,w.W,w.H).Contains(area)){MessageBox.Show("선택한 키텍 창 안의 로그 영역만 지정하세요.");return;}
        _cfg.Regions=new(){new(){X=area.X-w.X,Y=area.Y-w.Y,W=area.Width,H=area.Height}};
        _cfg.RegionsRelative=true;_cfg.Save();Restart(true);
    }

    private void Restart(bool resume)
    {
        var psi=new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath){UseShellExecute=false};
        psi.ArgumentList.Add("--wait-pid");psi.ArgumentList.Add(Environment.ProcessId.ToString());psi.ArgumentList.Add(resume?"--resume":"--setup");
        System.Diagnostics.Process.Start(psi);ExitApp();
    }

    private void ExitApp()
    {
        _statusTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _worker.Dispose();
        ExitThread();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(new SolidBrush(Color.FromArgb(30, 120, 60)), 1, 1, 30, 30);
            using var f = new Font("Arial", 15, FontStyle.Bold);
            g.DrawString("R", f, Brushes.White, 6, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
