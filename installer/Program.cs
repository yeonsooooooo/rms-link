using RmsLink.Shared;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
namespace RmsLinkInstaller;
static class Program
{
    static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RmsLink");
    [STAThread] static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(Root);
            using var mutex=new Mutex(true,@"Local\RmsLinkInstaller",out bool first);if(!first)return 0;
            using var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if(payload!=null && args.Length==2 && args[0]=="--verify-payload") {
                using var zip=new ZipArchive(payload);var files=zip.Entries.Select(x=>x.FullName).ToArray();
                if(!files.Any(f=>System.Text.RegularExpressions.Regex.IsMatch(f,@"^versions/\d+\.\d+\.\d+/RmsLink\.exe$"))||!files.Contains("RmsLinkLauncher.exe")||!files.Contains("bootstrap.json"))throw new Exception("설치 구성 누락");
                File.WriteAllText(args[1],JsonSerializer.Serialize(new {passed=true,files=files.Length,installed=false}));return 0;
            }
            if(payload!=null) {
                if(Process.GetProcessesByName("RmsLink").Length>0){MessageBox.Show("실행 중인 RmsLink를 종료한 다음 설치해 주세요.");return 1;}
                using var zip=new ZipArchive(payload);
                foreach(var entry in zip.Entries){var target=Path.GetFullPath(Path.Combine(Root,entry.FullName));if(!target.StartsWith(Root+Path.DirectorySeparatorChar))throw new Exception("설치 경로 오류");if(entry.FullName.EndsWith('/'))continue;Directory.CreateDirectory(Path.GetDirectoryName(target));entry.ExtractToFile(target,true);}
                using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,"install.json")));
                WriteCurrent(manifest.RootElement.GetProperty("version").GetString());
                // Unicode Windows shell API; no locale-dependent script host.
                foreach(var folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolderOption.Create),Environment.GetFolderPath(Environment.SpecialFolder.Programs,Environment.SpecialFolderOption.Create)}) {
                    Directory.CreateDirectory(folder);
                    ShortcutFile.Create(Path.Combine(folder,"RmsLink 연결 설정.lnk"),Path.Combine(Root,"RmsLinkLauncher.exe"),"호텔 ID · 키텍 앱 선택",Path.Combine(Root,"versions",ReadCurrent(),"assets","dashboard.ico"),"--setup");
                    using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,"bootstrap.json")));
                    string server=config.RootElement.GetProperty("serverUrl").GetString();
                    if(!Uri.TryCreate(server,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new Exception("대시보드 주소 오류");
                    File.WriteAllText(Path.Combine(folder,"RmsLink 대시보드.url"),"[InternetShortcut]\r\nURL="+uri.GetLeftPart(UriPartial.Authority)+"/\r\nIconFile="+Path.Combine(Root,"versions",ReadCurrent(),"assets","dashboard.ico")+"\r\nIconIndex=0\r\n",System.Text.Encoding.Unicode);
                }
                using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");key.SetValue("RmsLink","\""+Path.Combine(Root,"RmsLinkLauncher.exe")+"\"");
                if(args.Length==2&&args[0]=="--install-test") {
                    var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    bool dashboard=File.ReadAllText(Path.Combine(desktop,"RmsLink 대시보드.url")).Contains("URL=https://");
                    bool connection=File.Exists(Path.Combine(desktop,"RmsLink 연결 설정.lnk"));
                    bool icon=File.Exists(Path.Combine(Root,"versions",ReadCurrent(),"assets","dashboard.ico"));
                    if(!dashboard||!connection||!icon)throw new Exception("바탕화면 바로가기 검증 실패");
                    File.WriteAllText(args[1],JsonSerializer.Serialize(new{passed=true,installed=true,version=ReadCurrent(),dashboardShortcut=dashboard,connectionShortcut=connection,customIcon=icon,physicalKeytechVerified=false}));return 0;
                }
            }
            bool update=args.Length==2 && args[0]=="--update";
            if(update){try{using var old=Process.GetProcessById(int.Parse(args[1]));if(!old.WaitForExit(45000))throw new Exception("RmsLink 종료를 기다리고 있습니다. 다시 업데이트해 주세요.");}catch(ArgumentException){}}
            string pending=Path.Combine(Root,"pending.json");
            if(File.Exists(pending)) UpdateActivation.Apply(Root,v=>Launch(v,true),TimeSpan.FromSeconds(90));
            else Launch(ReadCurrent(),false,args.Contains("--setup"));
            return 0;
        }catch(Exception ex){File.AppendAllText(Path.Combine(Root,"installer.log"),DateTimeOffset.Now+" "+ex+Environment.NewLine);MessageBox.Show("RmsLink 설치/업데이트 실패\n"+ex.Message,"RmsLink");return 1;}
    }
    static void ValidateVersion(string v)=>UpdateActivation.ValidateVersion(v);
    static string ReadCurrent()=>UpdateActivation.ReadCurrent(Root);
    static void WriteCurrent(string v)=>UpdateActivation.WriteCurrent(Root,v);
    static Process Launch(string v,bool resume,bool setup=false){ValidateVersion(v);var p=new ProcessStartInfo(Path.Combine(Root,"versions",v,"RmsLink.exe")){UseShellExecute=false,WorkingDirectory=Root};if(resume)p.ArgumentList.Add("--resume");if(setup)p.ArgumentList.Add("--setup");return Process.Start(p);}
}
