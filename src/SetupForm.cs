namespace RmsLink;
public sealed class SetupForm:Form
{
    readonly AppConfig cfg;readonly TextBox hotel;readonly Label target;readonly CheckBox auto,share;bool picked;
    public SetupForm(AppConfig config)
    {
        cfg=config;Text="RmsLink · 호텔과 키텍 연결";StartPosition=FormStartPosition.CenterScreen;ClientSize=new(620,535);Font=new("맑은 고딕",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
        var intro=new Label{Text="호텔 ID를 입력하고 키텍 아이콘을 선택하세요.",Location=new(24,20),AutoSize=true,Font=new("맑은 고딕",13,FontStyle.Bold)};
        var hint=new Label{Text="1. 호텔 ID · 저장한 연결은 Windows 로그인 후 자동 재개됩니다.",Location=new(24,63),AutoSize=true};
        hotel=new(){Text=cfg.HotelId,PlaceholderText="예: 9 또는 10",Location=new(24,92),Width=565};
        var pick=new Button{Text="2. 바탕화면 아이콘에서 키텍 앱 선택",Location=new(24,143),Size=new(565,45)};
        pick.Click+=(_,_)=>{using var picker=new AppPickerForm();if(picker.ShowDialog()==DialogResult.OK){cfg.SelectedApp=picker.Selection;cfg.Regions.Clear();cfg.RegionsRelative=true;picked=true;UpdateTarget();}};
        target=new(){Location=new(24,201),Size=new(565,58),ForeColor=Color.FromArgb(25,100,85)};UpdateTarget();
        share=new(){Text="선택한 키텍 화면 이미지도 대시보드로 전송",Checked=cfg.ShareEvidence,Location=new(24,269),AutoSize=true};
        auto=new(){Text="Windows 로그인 시 실행 · 자동 업데이트",Checked=cfg.AutoUpdate,Location=new(24,304),AutoSize=true};
        var restore=new CheckBox{Text="최소화되면 키텍 창 자동 복원 · 화면에 다시 표시됩니다",Checked=cfg.RestoreMinimized,Location=new(24,339),AutoSize=true};
        var note=new Label{Text="선택한 앱의 화면만 읽습니다. 연결 후 대시보드에서\n판독 결과와 실제 문·키 동작을 대조해 주세요.",Location=new(24,379),Size=new(565,45),ForeColor=Color.DimGray};
        var ok=new Button{Text="3. 이 호텔의 키텍 연결 시작",Location=new(24,459),Size=new(565,46),BackColor=Color.FromArgb(25,100,85),ForeColor=Color.White};
        ok.Click+=(_,_)=>{
            var id=hotel.Text.Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,49}$")){MessageBox.Show("호텔 ID를 숫자 또는 영문·숫자·하이픈으로 입력하세요.");return;}
            if(cfg.SelectedApp==null || (cfg.HotelId.Length>0&&cfg.HotelId!=id&&!picked)){MessageBox.Show("이 호텔에서 사용할 키텍 앱을 직접 선택해 주세요.");return;}
            cfg.HotelId=id;cfg.AutoUpdate=auto.Checked;cfg.ShareEvidence=share.Checked;cfg.RestoreMinimized=restore.Checked;cfg.Save();if(auto.Checked)AutoStart.Register();else AutoStart.Unregister();
            try{DesktopLinks.Ensure(cfg);}catch(Exception ex){MessageBox.Show("연결은 저장했습니다. 바탕화면 바로가기 생성 실패: "+ex.Message);}
            DialogResult=DialogResult.OK;Close();
        };
        Controls.AddRange(new Control[]{intro,hint,hotel,pick,target,share,auto,restore,note,ok});AcceptButton=ok;
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
