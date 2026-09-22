namespace RmsLink;
public sealed class SetupForm:Form
{
    readonly AppConfig cfg;
    readonly TextBox hotel, registration;
    readonly Label target, status;
    readonly CheckBox auto, startup, share;
    bool picked, checking;
    public SetupForm(AppConfig config)
    {
        cfg=config;AutoScroll=true;Text="RmsLink · 호텔과 키텍 연결";StartPosition=FormStartPosition.CenterScreen;ClientSize=new(640,690);Font=new("맑은 고딕",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
        var intro=new Label{Text="설치 다음 단계: 호텔·키텍·서버 연결 확인",Location=new(24,20),AutoSize=true,Font=new("맑은 고딕",13,FontStyle.Bold)};
        var hint=new Label{Text="1. 실제 호텔 ID를 입력하세요.",Location=new(24,63),AutoSize=true};
        hotel=new(){Text=cfg.HotelId,PlaceholderText="예: 9 또는 10",Location=new(24,90),Width=590};
        var pick=new Button{Text="2. 바탕화면 아이콘에서 키텍 앱 선택",Location=new(24,135),Size=new(590,42)};
        pick.Click+=(_,_)=>{try{using var picker=new AppPickerForm();if(picker.ShowDialog()==DialogResult.OK){cfg.SelectedApp=picker.Selection;cfg.Regions.Clear();cfg.RegionsRelative=true;picked=true;UpdateTarget();}}catch(Exception ex){status.Text="APP_SELECT: "+ex.Message;}};
        target=new(){Location=new(24,189),Size=new(590,54),ForeColor=Color.FromArgb(25,100,85)};UpdateTarget();
        share=new(){Text="선택한 키텍 화면 이미지도 대시보드로 전송",Checked=cfg.ShareEvidence,Location=new(24,252),AutoSize=true};
        startup=new(){Text="Windows 로그인 시 자동 실행",Checked=AutoStart.IsRegistered(),Location=new(24,282),AutoSize=true};
        auto=new(){Text="검증된 앱과 판독 규칙 자동 업데이트",Checked=cfg.AutoUpdate,Location=new(24,312),AutoSize=true};
        var restore=new CheckBox{Text="최소화되면 키텍 창 자동 복원",Checked=cfg.RestoreMinimized,Location=new(24,342),AutoSize=true};
        var codeLabel=new Label{Text="등록권이 만료됐을 때만: 대시보드에서 새 등록 코드를 발급해 입력",Location=new(24,382),AutoSize=true};
        registration=new(){PlaceholderText="정상 설치는 비워 두세요",Location=new(24,407),Width=590,UseSystemPasswordChar=true};
        status=new(){Text="서버 연결을 확인한 뒤 판독 미리보기를 엽니다.\n마지막으로 대시보드에서 실제 문·키 동작을 대조하세요.",Location=new(24,448),Size=new(590,92),ForeColor=Color.DimGray};
        var logs=new Button{Text="실패 진단 파일 저장",Location=new(24,550),Size=new(210,36)};
        logs.Click+=(_,_)=>{try{MessageBox.Show("진단 파일: "+Diagnostics.ExportDiagnostics());}catch(Exception ex){MessageBox.Show(ex.Message);}};
        var ok=new Button{Text="3. 서버 연결 확인 후 시작",Location=new(24,605),Size=new(590,48),BackColor=Color.FromArgb(25,100,85),ForeColor=Color.White};
        ok.Click+=async (_,_)=>{
            var id=hotel.Text.Trim();
            if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,49}$")){status.Text="호텔 ID를 숫자 또는 영문·숫자·하이픈으로 입력하세요.";return;}
            if(cfg.SelectedApp==null||(cfg.HotelId.Length>0&&cfg.HotelId!=id&&!picked)){status.Text="이 호텔에서 사용할 키텍 앱을 직접 선택해 주세요.";return;}
            if(registration.Text.Trim().Length>0&&!System.Text.RegularExpressions.Regex.IsMatch(registration.Text.Trim(),@"^[a-f0-9]{48}$")){status.Text="등록 코드는 48자리입니다. 대시보드에서 발급한 코드를 확인하세요.";return;}
            checking=true;foreach(Control c in Controls)c.Enabled=false;status.Enabled=true;status.Text="HTTPS 서버 → 기기 등록 → 호텔 연결 확인 중…\n환경에 따라 최대 75초가 걸릴 수 있습니다.";
            try {
                cfg.SetupCompleted=false;cfg.HotelId=id;cfg.AutoUpdate=auto.Checked;cfg.ShareEvidence=share.Checked;cfg.RestoreMinimized=restore.Checked;
                if(registration.Text.Trim().Length>0)cfg.EnrollmentCode=cfg.EnrollmentCodeOverride=registration.Text.Trim();
                cfg.Save();
                using(var sink=new ControlSink(cfg))status.Text=await sink.CheckConnection();
                cfg.SetupCompleted=true;cfg.Save();
                try {if(startup.Checked)AutoStart.Register();else AutoStart.Unregister();DesktopLinks.Ensure(cfg);}
                catch(Exception ex){MessageBox.Show("서버 연결은 확인했습니다. 자동 시작·바로가기 설정 확인: "+ex.Message);}
                checking=false;DialogResult=DialogResult.OK;Close();
            } catch(Exception ex) {
                status.Text="연결 미완료 · "+ex.Message;status.ForeColor=Color.Firebrick;Logger.Error(status.Text);
            } finally {checking=false;foreach(Control c in Controls)c.Enabled=true;}
        };
        FormClosing+=(_,e)=>{if(checking)e.Cancel=true;};
        Controls.AddRange(new Control[]{intro,hint,hotel,pick,target,share,startup,auto,restore,codeLabel,registration,status,logs,ok});AcceptButton=ok;
    }
    protected override void OnShown(EventArgs e)
    {
        // Hotel PCs often use small displays or 125-150% scaling. Keep the final button reachable.
        var area=Screen.FromControl(this).WorkingArea;
        if(Width>area.Width-24||Height>area.Height-24) {
            Size=new(Math.Min(Width,area.Width-24),Math.Min(Height,area.Height-24));
            Location=new(area.X+(area.Width-Width)/2,area.Y+(area.Height-Height)/2);
        }
        base.OnShown(e);
    }
    void UpdateTarget()=>target.Text=cfg.SelectedApp==null?"아직 키텍 앱을 선택하지 않았습니다.":"선택됨: "+cfg.SelectedApp.Title+"\n"+Path.GetFileName(cfg.SelectedApp.Executable);
}
public static class AutoStart
{
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Register(){using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);string launcher=Path.Combine(AppConfig.InstallDir,"RmsLinkLauncher.exe");key.SetValue("RmsLink",$"\"{(File.Exists(launcher)?launcher:Application.ExecutablePath)}\"");}
    public static void Unregister(){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey,true);key?.DeleteValue("RmsLink",false);}
    public static bool IsRegistered(){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("RmsLink")!=null;}
}
