using RmsLink.Shared;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
namespace RmsLinkInstaller;
static class Program
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RmsLink");
    static StageReport Report = new(Path.Combine(Root, "installation-status.json"));
    static Label progress;
    static string previousMessage;
    static void Step(string code, string message) { if(previousMessage!=null)Report.Set(Report.Stage,"passed",previousMessage);previousMessage=message;Report.Set(code, "running", message); if (progress != null) { progress.Text = message; progress.Refresh(); Application.DoEvents(); } }
    [STAThread] static int Main(string[] args)
    {
        string testReport = args.Length == 2 && (args[0] == "--install-test" || args[0] == "--verify-payload") ? args[1] : null;
        Form window = null;
        try {
            // The bootstrap runtime itself can fail before Main: the independent collector covers that case.
            if(args.Contains("--collectlogs")) {
                var output=SupportBundle.Export(Root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RmsLink"),Path.GetTempPath());
                MessageBox.Show("진단 파일: "+output);return 0;
            }
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if(payload==null)Report=new(Path.Combine(Root,"launcher-status.json"));
            Step("START", "설치 관리자를 시작했습니다.");
            Directory.CreateDirectory(Root);
            using var mutex = new Mutex(true, @"Local\RmsLinkInstaller", out bool first);
            if (!first) throw new Exception("다른 설치 또는 업데이트가 진행 중입니다. 완료 후 다시 실행하세요.");
            if (payload != null) {
                if (testReport == null) {
                    Application.EnableVisualStyles();
                    window = new Form { Text = "RmsLink 설치 진행", ClientSize = new(570,155), StartPosition = FormStartPosition.CenterScreen, ControlBox = false };
                    progress = new Label { Dock = DockStyle.Fill, Padding = new(24), Font = new("맑은 고딕", 11), Text = "설치 준비 중… 잠시 기다려 주세요." };
                    window.Controls.Add(progress); window.Show(); Application.DoEvents();
                }
                Step("ENVIRONMENT", "1/6 Windows 버전과 설치 경로를 확인합니다.");
                if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,17763) || !Environment.Is64BitOperatingSystem)
                    throw new Exception("Windows 10 1809 이상, 64비트 Windows가 필요합니다.");
                using var zip = new ZipArchive(payload);
                // Extract into an isolated directory. A broken ZIP cannot damage a working installation.
                var staging = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try {
                    Step("EXTRACT", "2/6 설치 파일을 검사하고 압축을 풉니다.");
                    long expanded = 0;
                    foreach (var entry in zip.Entries) {
                        var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                        if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("설치 경로 오류");
                        expanded += entry.Length; if (expanded > 1_500_000_000) throw new Exception("설치 파일 크기 한도 초과");
                        if (entry.FullName.EndsWith('/')) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); entry.ExtractToFile(target, false);
                    }
                    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging,"install.json")));
                    string version = manifest.RootElement.GetProperty("version").GetString(); UpdateActivation.ValidateVersion(version);
                    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging,"bootstrap.json")));
                    string server = config.RootElement.GetProperty("serverUrl").GetString();
                    if (!Uri.TryCreate(server,UriKind.Absolute,out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "") throw new Exception("대시보드 주소 오류");
                    foreach (var file in new[]{"RmsLinkLauncher.exe", $"versions/{version}/RmsLink.exe", $"versions/{version}/assets/dashboard.ico"})
                        if (!File.Exists(Path.Combine(staging,file))) throw new Exception("설치 구성 누락: " + file);
                    if (args.FirstOrDefault() == "--verify-payload") { WriteResult(testReport, new {passed=true, files=zip.Entries.Count, installed=false}); return 0; }
                    if (Process.GetProcessesByName("RmsLink").Length > 0) throw new Exception("실행 중인 RmsLink를 트레이 메뉴에서 종료한 다음 다시 설치하세요. 기존 설정은 보존됩니다.");
                    Step("RUNTIME", "3/6 이 PC에서 프로그램이 실행되는지 점검합니다.");
                    RunRuntime(Path.Combine(staging,"versions",version,"RmsLink.exe"), Path.Combine(Root,"install-runtime.json"), version);
                    Step("ACTIVATE", "4/6 검사한 프로그램을 설치합니다.");
                    Commit(staging, version);
                    Step("SHORTCUTS", "5/6 바로가기와 자동 시작을 준비합니다.");
                    var warnings = new List<string>();
                    foreach (var folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.Programs)}) {
                        try {
                            Directory.CreateDirectory(folder);
                            ShortcutFile.Create(Path.Combine(folder,"RmsLink 연결 설정.lnk"), Path.Combine(Root,"RmsLinkLauncher.exe"), "호텔 ID · 키텍 앱 선택", Path.Combine(Root,"versions",version,"assets","dashboard.ico"), "--setup");
                            File.WriteAllText(Path.Combine(folder,"RmsLink 대시보드.url"), "[InternetShortcut]\r\nURL=" + uri.GetLeftPart(UriPartial.Authority) + "/\r\nIconFile=" + Path.Combine(Root,"versions",version,"assets","dashboard.ico") + "\r\nIconIndex=0\r\n", System.Text.Encoding.Unicode);
                        } catch (Exception ex) { warnings.Add("바로가기: " + ex.Message); }
                    }
                    // A reinstall must respect an existing user's automatic-start preference.
                    if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RmsLink","config.json"))) {
                        try { using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); key.SetValue("RmsLink", "\"" + Path.Combine(Root,"RmsLinkLauncher.exe") + "\""); }
                        catch (Exception ex) { warnings.Add("자동 시작: " + ex.Message); }
                    }
                    Report.Set("SHORTCUTS", warnings.Count == 0 ? "passed" : "warning", string.Join("\n",warnings));
                    if (testReport != null) {
                        if (warnings.Count > 0) throw new Exception(string.Join("\n",warnings));
                        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        WriteResult(testReport,new {passed=true,installed=true,version,dashboardShortcut=File.Exists(Path.Combine(desktop,"RmsLink 대시보드.url")),connectionShortcut=File.Exists(Path.Combine(desktop,"RmsLink 연결 설정.lnk")),customIcon=true,runtimeVerified=true,physicalKeytechVerified=false});
                        Report.Set("COMPLETE","passed","파일 설치와 실행 점검 완료. 호텔 연결과 실제 문·키 대조는 아직 수행하지 않았습니다."); return 0;
                    }
                    if (warnings.Count > 0) MessageBox.Show("프로그램은 설치했습니다. 일부 설정을 확인하세요.\n" + string.Join("\n",warnings));
                } finally { try { Directory.Delete(staging,true); } catch { } }
                Step("APP_START", "6/6 연결 설정 창을 엽니다.");
                LaunchChecked();
                Report.Set("COMPLETE","passed","설치 및 연결 설정 창 실행 완료. 설정 창에서 서버 연결을 확인하세요.");
                return 0;
            }
            bool update = args.Length == 2 && args[0] == "--update";
            if (update) { try { using var old=Process.GetProcessById(int.Parse(args[1])); if(!old.WaitForExit(45000))throw new Exception("RmsLink 종료를 기다리고 있습니다. 다시 업데이트해 주세요."); } catch(ArgumentException) { } }
            if(File.Exists(Path.Combine(Root,"pending.json"))) UpdateActivation.Apply(Root,v=>Launch(v,true),TimeSpan.FromSeconds(90));
            else Launch(UpdateActivation.ReadCurrent(Root),false,args.Contains("--setup"));
            return 0;
        } catch(Exception ex) {
            Report.Set(Report.Stage,"failed",ex.Message);
            try { File.AppendAllText(Path.Combine(Root,"installer.log"),DateTimeOffset.Now+" ["+Report.Stage+"] "+ex+Environment.NewLine); } catch { }
            if(testReport!=null) { try { WriteResult(testReport,new{passed=false,stage=Report.Stage,error=ex.Message}); } catch { } }
            else MessageBox.Show("RmsLink 설치/실행을 완료하지 못했습니다.\n단계: "+Report.Stage+"\n\n"+ex.Message+"\n\n진단 기록: "+Root+"\n독립 진단 도구 collect-logs.cmd로 기록을 모을 수 있습니다.","RmsLink");
            return 1;
        } finally { window?.Dispose(); }
    }
    static void WriteResult(string path, object result) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); File.WriteAllText(path,JsonSerializer.Serialize(result)); }
    static void RunRuntime(string exe,string output,string version) {
        File.Delete(output);
        var start=new ProcessStartInfo(exe){UseShellExecute=false}; start.ArgumentList.Add("--runtime-test"); start.ArgumentList.Add(output);
        using var p=Process.Start(start)??throw new Exception("실행 점검 프로세스 시작 실패");
        if(!p.WaitForExit(45000)){p.Kill(true);p.WaitForExit(5000);throw new Exception("실행 점검 시간 초과. 보안 프로그램의 차단 기록을 확인하세요.");}
        if(p.ExitCode!=0||!File.Exists(output))throw new Exception("실행 점검 실패. install-runtime.json 또는 Windows 보안 차단 기록을 확인하세요.");
        using var result=JsonDocument.Parse(File.ReadAllText(output));
        if(!result.RootElement.GetProperty("passed").GetBoolean()||result.RootElement.GetProperty("version").GetString()!=version)throw new Exception("실행 점검 결과 또는 버전 불일치");
    }
    static void Commit(string staging,string version) {
        string target=Path.Combine(Root,"versions",version), backup=target+".backup-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        var prior=File.Exists(Path.Combine(Root,"current.txt"))?File.ReadAllText(Path.Combine(Root,"current.txt")):null;
        var files=new[]{"RmsLinkLauncher.exe","bootstrap.json","install.json"};
        var saved=files.ToDictionary(f=>f,f=>File.Exists(Path.Combine(Root,f))?File.ReadAllBytes(Path.Combine(Root,f)):null);
        bool moved=false;
        try {
            if(Directory.Exists(target))Directory.Move(target,backup);
            Directory.Move(Path.Combine(staging,"versions",version),target);moved=true;
            foreach(var f in files) { File.Copy(Path.Combine(staging,f),Path.Combine(Root,f+".new"),true);File.Move(Path.Combine(Root,f+".new"),Path.Combine(Root,f),true); }
            UpdateActivation.WriteCurrent(Root,version);
        } catch {
            if(moved)Directory.Delete(target,true);
            if(Directory.Exists(backup))Directory.Move(backup,target);
            foreach(var f in files) {if(saved[f]!=null)File.WriteAllBytes(Path.Combine(Root,f),saved[f]);else File.Delete(Path.Combine(Root,f));}
            if(prior!=null)File.WriteAllText(Path.Combine(Root,"current.txt"),prior);else File.Delete(Path.Combine(Root,"current.txt"));
            throw;
        }
        try{if(Directory.Exists(backup))Directory.Delete(backup,true);}catch{}
    }
    static void LaunchChecked() {
        string ready=Path.Combine(Root,"startup-"+Guid.NewGuid().ToString("N")+".json");
        using var p=Launch(UpdateActivation.ReadCurrent(Root),false,true,ready);
        var clock=Stopwatch.StartNew();
        try { while(!p.HasExited&&!File.Exists(ready)&&clock.Elapsed<TimeSpan.FromSeconds(30)){Thread.Sleep(100);Application.DoEvents();}
            if(!File.Exists(ready))throw new Exception("연결 설정 창의 시작을 확인하지 못했습니다. 앱 로그와 Windows 보안 기록을 확인하세요.");
        } finally {try{File.Delete(ready);}catch{}}
    }
    static Process Launch(string v,bool resume,bool setup=false,string ready=null) {
        UpdateActivation.ValidateVersion(v);
        var p=new ProcessStartInfo(Path.Combine(Root,"versions",v,"RmsLink.exe")){UseShellExecute=false,WorkingDirectory=Root};
        if(resume)p.ArgumentList.Add("--resume");if(setup)p.ArgumentList.Add("--setup");
        if(ready!=null){p.ArgumentList.Add("--startup-report");p.ArgumentList.Add(ready);}
        return Process.Start(p)??throw new Exception("프로그램 실행 실패");
    }
}
