namespace RmsLink;

/// <summary>첫 실행 설정: 호텔 ID 입력 + 로그 영역 지정</summary>
public sealed class SetupForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _hotelBox;
    private readonly CheckBox _autoStartChk;
    private readonly Label _regionLabel;

    public SetupForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "RmsLink 최초 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 260);
        Font = new Font("맑은 고딕", 9.5f);

        var l1 = new Label
        {
            Text = "호텔 구분 ID (예: gwangju-ilbunji, yeoju-ace)",
            Location = new Point(20, 18), AutoSize = true
        };
        _hotelBox = new TextBox
        {
            Location = new Point(20, 42), Width = 390,
            Text = SanitizeDefault(Environment.MachineName)
        };

        _regionLabel = new Label
        {
            Text = "지정된 로그 영역: 없음",
            Location = new Point(20, 80), AutoSize = true, ForeColor = Color.Firebrick
        };

        var regionBtn = new Button
        {
            Text = "로그 영역 지정 (화면에서 드래그)",
            Location = new Point(20, 104), Size = new Size(390, 34)
        };
        regionBtn.Click += (_, _) => PickRegions();

        _autoStartChk = new CheckBox
        {
            Text = "Windows 시작 시 자동 실행 (권장)",
            Location = new Point(20, 150), AutoSize = true, Checked = true
        };

        var okBtn = new Button
        {
            Text = "저장하고 연동 시작",
            Location = new Point(20, 190), Size = new Size(250, 40)
        };
        okBtn.Click += (_, _) => Finish();

        var cancelBtn = new Button
        {
            Text = "취소",
            Location = new Point(280, 190), Size = new Size(130, 40),
            DialogResult = DialogResult.Cancel
        };

        Controls.AddRange(new Control[] { l1, _hotelBox, _regionLabel, regionBtn, _autoStartChk, okBtn, cancelBtn });
        UpdateRegionLabel();
    }

    private static string SanitizeDefault(string s)
    {
        var t = new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return string.IsNullOrEmpty(t) ? "hotel-1" : t;
    }

    private void PickRegions()
    {
        _cfg.Regions.Clear();
        Hide();
        try
        {
            while (true)
            {
                using var sel = new RoiSelectorForm();
                if (sel.ShowDialog() != DialogResult.OK) break;
                var r = sel.SelectedScreenRect;
                _cfg.Regions.Add(new CaptureRegion { X = r.X, Y = r.Y, W = r.Width, H = r.Height });

                if (MessageBox.Show(
                        "로그 영역을 하나 더 추가할까요?\n(로그 리스트가 2개인 RMS에서만 '예')",
                        "RmsLink", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                    break;
            }
        }
        finally
        {
            Show();
            Activate();
            UpdateRegionLabel();
        }
    }

    private void UpdateRegionLabel()
    {
        if (_cfg.Regions.Count == 0)
        {
            _regionLabel.Text = "지정된 로그 영역: 없음";
            _regionLabel.ForeColor = Color.Firebrick;
        }
        else
        {
            _regionLabel.Text = "지정된 로그 영역: " +
                string.Join(", ", _cfg.Regions.Select(r => $"{r.W}x{r.H}"));
            _regionLabel.ForeColor = Color.ForestGreen;
        }
    }

    private void Finish()
    {
        var hotel = _hotelBox.Text.Trim();
        if (hotel.Length == 0)
        {
            MessageBox.Show("호텔 구분 ID를 입력해 주세요.", "RmsLink");
            return;
        }
        if (_cfg.Regions.Count == 0)
        {
            MessageBox.Show("로그 영역을 먼저 지정해 주세요.", "RmsLink");
            return;
        }
        _cfg.HotelId = hotel;
        _cfg.Save();
        if (_autoStartChk.Checked) AutoStart.Register();
        else AutoStart.Unregister();
        DialogResult = DialogResult.OK;
        Close();
    }
}

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "RmsLink";

    public static void Register()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.SetValue(Name, $"\"{Application.ExecutablePath}\"");
        }
        catch (Exception ex) { Logger.Error("자동시작 등록 실패: " + ex.Message); }
    }

    public static void Unregister()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(Name, false);
        }
        catch (Exception ex) { Logger.Error("자동시작 해제 실패: " + ex.Message); }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(Name) != null;
        }
        catch { return false; }
    }
}
