using System.Diagnostics;
namespace RmsLink;
public sealed class AppPickerForm:Form
{
    readonly ListView apps=new(){Dock=DockStyle.Fill,View=View.LargeIcon,MultiSelect=false};
    readonly ListView windows=new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false};
    readonly PictureBox preview=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(237,244,239)};
    readonly Label info=new(){Dock=DockStyle.Top,Height=55,Text="① 평소 키텍을 열 때 누르는 바탕화면 아이콘을 두 번 클릭하세요.\n② 실행된 창을 선택하고 미리보기를 확인하세요."};
    readonly Button confirm=new(){Text="이 화면이 키텍 앱입니다 · 연결",Dock=DockStyle.Bottom,Height=44,Enabled=false};
    readonly ImageList icons=new(){ImageSize=new(48,48),ColorDepth=ColorDepth.Depth32Bit};
    readonly WindowCapture capture=new();
    string launchPath="",executable="";WindowCandidate selected;int generation;
    public SelectedApplication Selection {get;private set;}
    public AppPickerForm()
    {
        Text="키텍 앱 선택 · 바탕화면 아이콘으로 찾기";ClientSize=new(1000,700);MinimumSize=new(840,580);StartPosition=FormStartPosition.CenterParent;Font=new("맑은 고딕",10);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=45};
        var browse=new Button{Text="아이콘/실행 파일 찾기",Width=210};browse.Click+=(_,_)=>Browse();
        var refresh=new Button{Text="실행 중인 창 새로고침",Width=220};refresh.Click+=(_,_)=>RefreshWindows();
        actions.Controls.AddRange(new Control[]{browse,refresh});
        var split=new SplitContainer{Dock=DockStyle.Fill,Size=new(960,540),SplitterDistance=340};
        var right=new SplitContainer{Dock=DockStyle.Fill,Size=new(600,540),Orientation=Orientation.Horizontal,SplitterDistance=210};
        windows.Columns.Add("실행 중인 앱",190);windows.Columns.Add("창 이름",420);
        split.Panel1.Controls.Add(apps);right.Panel1.Controls.Add(windows);right.Panel2.Controls.Add(preview);split.Panel2.Controls.Add(right);
        Controls.Add(split);Controls.Add(confirm);Controls.Add(actions);Controls.Add(info);
        apps.LargeImageList=icons;LoadDesktop();RefreshWindows();
        apps.DoubleClick+=(_,_)=>{if(apps.SelectedItems.Count>0)Launch((string)apps.SelectedItems[0].Tag);};
        windows.SelectedIndexChanged+=async (_,_)=>{
            int current=++generation;confirm.Enabled=false;selected=null;
            preview.Image?.Dispose();preview.Image=null;
            if(windows.SelectedItems.Count==0)return;
            var w=(WindowCandidate)windows.SelectedItems[0].Tag;
            if(string.IsNullOrWhiteSpace(w.Executable)){info.Text="실행 파일 경로에 접근할 수 없습니다. 키텍과 RmsLink의 실행 권한을 확인하세요.";return;}
            if(w.Minimized){info.Text="키텍 창이 최소화되어 있습니다. 창을 복원하고 새로고침하세요.";return;}
            try {var result=await capture.Read(w);if(IsDisposed||current!=generation){result.Image.Dispose();return;}preview.Image=result.Image;
                selected=w;confirm.Enabled=true;info.Text="선택한 앱: "+w.Process+" · "+w.Title+"\n미리보기가 실제 키텍 화면인지 확인한 후 아래 연결 버튼을 누르세요.";
            }catch(Exception ex){if(!IsDisposed&&current==generation)info.Text=ex.Message;}
        };
        confirm.Click+=(_,_)=>{if(selected==null)return;Selection=new(){Name="키텍 앱",Executable=selected.Executable,LaunchPath=string.IsNullOrEmpty(launchPath)?selected.Executable:launchPath,Title=selected.Title,Handle=selected.Handle,ProcessId=selected.ProcessId};DialogResult=DialogResult.OK;Close();};
    }
    void LoadDesktop()
    {
        foreach(var folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)}.Distinct()) {
            if(!Directory.Exists(folder))continue;
            foreach(var path in Directory.EnumerateFiles(folder).Where(p=>new[]{".lnk",".exe"}.Contains(Path.GetExtension(p).ToLowerInvariant()))) {
                if(Path.GetFileName(path).StartsWith("RmsLink",StringComparison.OrdinalIgnoreCase))continue;
                try{using var icon=Icon.ExtractAssociatedIcon(path);icons.Images.Add(path,icon?.ToBitmap()??SystemIcons.Application.ToBitmap());}catch{icons.Images.Add(path,SystemIcons.Application.ToBitmap());}
                apps.Items.Add(new ListViewItem(Path.GetFileNameWithoutExtension(path)){ImageKey=path,Tag=path});
            }
        }
    }
    void Browse(){using var d=new OpenFileDialog{Title="평소 사용하는 키텍 바로가기 선택",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Filter="앱 바로가기 / 실행 파일|*.lnk;*.exe",DereferenceLinks=false};if(d.ShowDialog()==DialogResult.OK)Launch(d.FileName);}
    async void Launch(string path)
    {
        try{
            string target=path;
            if(Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase)) {dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shortcut=shell.CreateShortcut(path);try{target=(string)shortcut.TargetPath;}finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}}
            if(!Path.GetExtension(target).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new Exception("Windows 프로그램의 .lnk 또는 .exe를 선택하세요.");
            launchPath=path;executable=target;
            var running=WindowProbe.All().Where(w=>string.Equals(w.Executable,target,StringComparison.OrdinalIgnoreCase)).ToList();
            if(running.Count>0)WindowProbe.Activate(running[0]);else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
            info.Text="앱을 실행했습니다. 키텍에 로그인한 뒤 ‘실행 중인 창 새로고침’을 누르세요.\n실제 키텍 창을 선택하면 미리보기가 표시됩니다.";
            await Task.Delay(1500);if(!IsDisposed)RefreshWindows();
        }catch(Exception ex){MessageBox.Show(ex.Message,"키텍 앱 선택");}
    }
    void RefreshWindows()
    {
        windows.Items.Clear();
        foreach(var w in WindowProbe.All().Where(w=>w.ProcessId!=Environment.ProcessId&&w.Process!="explorer").OrderByDescending(w=>string.Equals(w.Executable,executable,StringComparison.OrdinalIgnoreCase)))
            windows.Items.Add(new ListViewItem(new[]{w.Process,w.Title+(w.Minimized?" (최소화)":"")}){Tag=w});
    }
    protected override void Dispose(bool disposing){if(disposing){generation++;capture.Dispose();preview.Image?.Dispose();icons.Dispose();}base.Dispose(disposing);}
}
public static class DesktopLinks
{
    public static void Ensure(AppConfig cfg)
    {
        var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolderOption.Create);
        Directory.CreateDirectory(desktop);
        var icon=Path.Combine(AppContext.BaseDirectory,"assets","dashboard.ico");
        File.WriteAllText(Path.Combine(desktop,"RmsLink 대시보드.url"),"[InternetShortcut]\r\nURL="+cfg.ServerUrl+"/\r\nIconFile="+icon+"\r\nIconIndex=0\r\n");
        var launcher=Path.Combine(AppConfig.InstallDir,"RmsLinkLauncher.exe");
        Link(Path.Combine(desktop,"RmsLink 연결 설정.lnk"),File.Exists(launcher)?launcher:Application.ExecutablePath,"호텔 ID 확인 · 키텍 앱 선택",Path.Combine(AppContext.BaseDirectory,"assets","dashboard.ico"));
        if(cfg.SelectedApp==null)return;
        string destination=Path.Combine(desktop,"키텍 앱.lnk"),source=cfg.SelectedApp.LaunchPath;
        if(Path.GetExtension(source).Equals(".lnk",StringComparison.OrdinalIgnoreCase)&&File.Exists(source)) {
            if(!string.Equals(Path.GetFullPath(source),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase))File.Copy(source,destination,true);
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shortcut=shell.CreateShortcut(destination);
            try{shortcut.Description="키텍 객실 관리 앱 · RmsLink에서 선택한 프로그램";shortcut.IconLocation=Path.Combine(AppContext.BaseDirectory,"assets","keytech.ico")+",0";shortcut.Save();}finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
        } else Link(destination,cfg.SelectedApp.Executable,"키텍 객실 관리 앱",Path.Combine(AppContext.BaseDirectory,"assets","keytech.ico"));
    }
    static void Link(string path,string target,string description,string icon)
    {
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(path);
        try{link.TargetPath=target;link.WorkingDirectory=Path.GetDirectoryName(target);link.Description=description;link.IconLocation=icon+",0";link.Save();}
        finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
    }
}
