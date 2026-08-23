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

    public TrayContext(AppConfig cfg, OcrService ocr)
    {
        _cfg = cfg;
        _worker = new Worker(cfg, ocr);
        _worker.Start();

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
        menu.Items.Add(previewItem);
        menu.Items.Add(reselectItem);
        menu.Items.Add(_autoStartItem);
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
        string dbNote = cfg.DbConfigured ? "" : "  ※DB 미설정: 로컬 백업만 됩니다";
        _icon.ShowBalloonTip(3000, "RmsLink 시작",
            $"호텔 [{cfg.HotelId}] 이벤트 감시를 시작했습니다. (OCR: {_worker.OcrLang}){dbNote}",
            cfg.DbConfigured ? ToolTipIcon.Info : ToolTipIcon.Warning);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var s = _worker.Sink;
        string text =
            $"[{_cfg.HotelId}] OCR {_worker.OcrLang} · 발견 {_worker.EventsFound} · 전송 {s.SentCount} · 대기 {s.PendingCount}";
        _statusItem.Text = text + (s.LastError.Length > 0 ? " · DB오류!" : "");
        string tip = "RmsLink - " + text;
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
        var regions = new List<CaptureRegion>();
        while (true)
        {
            using var sel = new RoiSelectorForm();
            if (sel.ShowDialog() != DialogResult.OK) break;
            var r = sel.SelectedScreenRect;
            regions.Add(new CaptureRegion { X = r.X, Y = r.Y, W = r.Width, H = r.Height });
            if (MessageBox.Show("로그 영역을 하나 더 추가할까요?", "RmsLink",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                break;
        }
        if (regions.Count == 0) return;

        _cfg.Regions = regions;
        _cfg.Save();
        MessageBox.Show("영역이 저장되었습니다. 프로그램을 다시 시작합니다.", "RmsLink");
        Application.Restart();
        ExitApp();
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
