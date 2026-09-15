namespace RmsLink;
public sealed class SetupForm:Form
{
    readonly AppConfig cfg; readonly TextBox hotel;readonly Label regions;readonly CheckBox auto,share;bool picked;
    public SetupForm(AppConfig config)
    {
        cfg=config;Text="RmsLink · 호텔 연결";StartPosition=FormStartPosition.CenterScreen;ClientSize=new(530,400);Font=new("맑은 고딕",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
        var intro=new Label{Text="키텍 연동을 시작할 호텔 ID를 입력하세요.",Location=new(24,20),AutoSize=true,Font=new("맑은 고딕",13,FontStyle.Bold)};
        var hint=new Label{Text="예: 9 또는 10 · 실행할 때마다 호텔을 확인합니다.",Location=new(24,59),AutoSize=true};
        hotel=new(){Text="",PlaceholderText=string.IsNullOrEmpty(cfg.HotelId)?"hotel_id":"이전 호텔: "+cfg.HotelId,Location=new(24,85),Width=480};
        regions=new(){Location=new(24,131),AutoSize=true};UpdateRegions();
        var pick=new Button{Text="로그 영역 직접 지정",Location=new(24,161),Size=new(235,34)};pick.Click+=(_,_)=>Pick();
        var detect=new Button{Text="RMS 창 자동 탐색 사용",Location=new(269,161),Size=new(235,34)};detect.Click+=(_,_)=>{cfg.Regions.Clear();UpdateRegions();};
        share=new(){Text="선택한 RMS 화면·판독 내용을 맥 미니로 보내 진단",Checked=cfg.ShareEvidence,Location=new(24,213),AutoSize=true};
        auto=new(){Text="Windows 로그인 시 실행 · 자동 업데이트",Checked=true,Location=new(24,245),AutoSize=true};
        var note=new Label{Text="RMS 창이 하나이면 자동으로 찾습니다. 판독이 어려우면\n키텍 이벤트 로그 영역을 직접 지정해 주세요.",Location=new(24,276),Size=new(480,45),ForeColor=Color.DimGray};
        var ok=new Button{Text="호텔 연결 시작",Location=new(24,336),Size=new(480,42),BackColor=Color.FromArgb(25,100,85),ForeColor=Color.White};
        ok.Click+=(_,_)=>{
            var id=hotel.Text.Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,49}$")){MessageBox.Show("hotel_id를 숫자 또는 영문·숫자·하이픈으로 입력해 주세요.");return;}
            if(cfg.HotelId.Length>0&&cfg.HotelId!=id&&!picked)cfg.Regions.Clear();
            cfg.HotelId=id;cfg.AutoUpdate=auto.Checked;cfg.ShareEvidence=share.Checked;cfg.Save();if(auto.Checked)AutoStart.Register();else AutoStart.Unregister();DialogResult=DialogResult.OK;Close();
        };
        Controls.AddRange(new Control[]{intro,hint,hotel,regions,pick,detect,share,auto,note,ok});AcceptButton=ok;
    }
    void UpdateRegions()=>regions.Text=cfg.Regions.Count==0?"수집 방식: RMS 창 자동 탐색":"수집 방식: 지정 영역 "+cfg.Regions.Count+"개";
    void Pick(){Hide();try{using var selector=new RoiSelectorForm();if(selector.ShowDialog()==DialogResult.OK){picked=true;var r=selector.SelectedScreenRect;cfg.Regions=new(){new(){X=r.X,Y=r.Y,W=r.Width,H=r.Height}};}}finally{Show();UpdateRegions();}}
}
public static class AutoStart
{
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Register(){using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);string launcher=Path.Combine(AppConfig.InstallDir,"RmsLinkLauncher.exe");key.SetValue("RmsLink",$"\"{(File.Exists(launcher)?launcher:Application.ExecutablePath)}\"");}
    public static void Unregister(){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey,true);key?.DeleteValue("RmsLink",false);}
    public static bool IsRegistered(){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("RmsLink")!=null;}
}
