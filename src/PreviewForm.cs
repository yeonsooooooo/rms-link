namespace RmsLink;

/// <summary>실시간 정확도 확인 창: 캡처 이미지 + OCR/파싱 결과 + DB 상태</summary>
public sealed class PreviewForm : Form
{
    private readonly Worker _worker;
    private readonly AppConfig _cfg;
    private readonly ComboBox _regionCombo;
    private readonly PictureBox _pic;
    private readonly ListView _list;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _timer;

    public PreviewForm(Worker worker, AppConfig cfg)
    {
        _worker = worker;
        _cfg = cfg;

        Text = "RmsLink - 판독 미리보기";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 720);
        Font = new Font("맑은 고딕", 9f);
        MinimumSize = new Size(600, 500);

        _regionCombo = new ComboBox
        {
            Location = new Point(12, 12), Width = 120,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        for (int i = 0; i < cfg.Regions.Count; i++)
            _regionCombo.Items.Add($"영역 {i + 1}");
        if (_regionCombo.Items.Count > 0) _regionCombo.SelectedIndex = 0;
        _regionCombo.SelectedIndexChanged += (_, _) => RefreshView();

        var refreshBtn = new Button { Text = "새로고침", Location = new Point(142, 11), Size = new Size(80, 26) };
        refreshBtn.Click += (_, _) => RefreshView();

        var autoChk = new CheckBox { Text = "자동 갱신(2초)", Location = new Point(232, 14), AutoSize = true, Checked = true };

        var dbBtn = new Button { Text = "서버 연결 확인", Location = new Point(360, 11), Size = new Size(110, 26) };
        dbBtn.Click += async (_, _) =>
        {
            dbBtn.Enabled = false;
            _statusLabel.Text = "맥 미니 연결 확인 중...";
            try { _statusLabel.Text = await _worker.Sink.CheckConnection(); } catch(Exception ex) { _statusLabel.Text = "연결 실패: "+ex.Message; } finally { dbBtn.Enabled = true; }
        };

        _statusLabel = new Label
        {
            Location = new Point(12, 44), AutoSize = false,
            Size = new Size(830, 60), Text = "",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _pic = new PictureBox
        {
            Location = new Point(12, 108), Size = new Size(830, 200),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _list = new ListView
        {
            Location = new Point(12, 316), Size = new Size(830, 390),
            View = View.Details, FullRowSelect = true, GridLines = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _list.Columns.Add("판정", 70);
        _list.Columns.Add("객실", 55);
        _list.Columns.Add("코드", 110);
        _list.Columns.Add("시각", 80);
        _list.Columns.Add("OCR 원문 / 사유", 480);

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => { if (autoChk.Checked) RefreshView(); };
        _timer.Start();

        Controls.AddRange(new Control[] { _regionCombo, refreshBtn, autoChk, dbBtn, _statusLabel, _pic, _list });
        FormClosed += (_, _) => { _timer.Stop(); _pic.Image?.Dispose(); };

        RefreshView();
    }

    private void RefreshView()
    {
        int idx = Math.Max(0, _regionCombo.SelectedIndex);
        var snaps = _worker.SnapshotDiagnostics();
        if (idx >= snaps.Count) { foreach(var snap in snaps) snap.Image?.Dispose(); _statusLabel.Text=_worker.LastCaptureStatus+"\n"+_worker.Sink.LastError; return; }
        for(int i=0;i<snaps.Count;i++) if(i!=idx) snaps[i].Image?.Dispose();

        var (img, lines, at) = snaps[idx];

        var old = _pic.Image;
        _pic.Image = img;
        old?.Dispose();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var ld in lines)
        {
            ListViewItem item;
            if (ld.Event != null)
            {
                string state = ld.IsNew ? "전송 대기" : "중복";
                item = new ListViewItem(new[]
                {
                    state, ld.Event.Room, ld.Event.Code + (ld.Event.Fuzzy ? "*" : ""),
                    ld.Event.EventTime, ld.Text
                });
                item.BackColor = ld.IsNew ? Color.FromArgb(220, 245, 220) : Color.FromArgb(238, 238, 238);
            }
            else
            {
                item = new ListViewItem(new[] { "미확정", "-", "-", "-", $"{ld.Text}   ({ld.Reason})" });
                item.ForeColor = Color.Gray;
            }
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        var s = _worker.Sink;
        string err = s.LastError.Length > 0 ? $" | 오류: {s.LastError}" : "";
        _statusLabel.Text =
            _worker.LastCaptureStatus + "\n" + $"OCR: {_worker.OcrLang} | 마지막 판독: {(at == DateTime.MinValue ? "-" : at.ToString("HH:mm:ss"))} " +
            $"| OCR 실행 {_worker.OcrRuns}회 | 이벤트 {_worker.EventsFound}건 발견 | 맥 미니 전송 {s.SentCount}건, 대기 {s.PendingCount}건\n{err}";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
