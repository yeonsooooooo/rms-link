using System.Text.Json;
using System.IO.Compression;
namespace RmsLink;
public static class Diagnostics
{
    public static int RuntimeTest(string output,bool native=false)
    {
        try {
            var now=DateTimeOffset.Now;var parsed=EventParser.Parse("101 문열림 "+now.ToString("HH:mm:ss"),now,new(),out _);
            if(parsed.Count!=1||parsed[0].Code!="DOOR_OPEN")throw new Exception("Parser failure");
            var data=System.Text.Encoding.UTF8.GetBytes("rmslink-check");
            var encrypted=System.Security.Cryptography.ProtectedData.Protect(data,null,System.Security.Cryptography.DataProtectionScope.CurrentUser);
            if(!data.SequenceEqual(System.Security.Cryptography.ProtectedData.Unprotect(encrypted,null,System.Security.Cryptography.DataProtectionScope.CurrentUser)))throw new Exception("DPAPI failure");
            var captureTests=native?NativeCaptureTest(Path.GetDirectoryName(Path.GetFullPath(output))):null;
            File.WriteAllText(output,JsonDefaults.Serialize(new {passed=true,captureTests,version=Updater.Version,windows=Environment.OSVersion.ToString(),ocrLanguages=OcrService.AvailableLanguages(),physicalKeytechVerified=false}));return 0;
        }catch(Exception ex){File.WriteAllText(output,JsonDefaults.Serialize(new {passed=false,error=ex.ToString()}));return 1;}
    }
    public static int CaptureFixture()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var form=new Form{Text="RmsLink native test fixture",ClientSize=new(700,380),StartPosition=FormStartPosition.Manual,Location=new(30,30),BackColor=Color.White};
        form.Controls.Add(new Label{Text="101 DOOR OPEN 12:00:00",AutoSize=true,Location=new(40,50),Font=new("Arial",22)});
        form.Controls.Add(new Label{Text="102 KEY IN 12:00:01",AutoSize=true,Location=new(40,120),Font=new("Arial",22)});
        using var timer=new System.Windows.Forms.Timer{Interval=90000};timer.Tick+=(_,_)=>form.Close();timer.Start();Application.Run(form);return 0;
    }
    static object NativeCaptureTest(string folder)
    {
        using(var setup=new SetupForm(new())){setup.CreateControl();}
        using(var picker=new AppPickerForm()){picker.CreateControl();}
        using var fixture=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath,"--capture-fixture"){UseShellExecute=false});
        try {
            WindowCandidate w=null;for(int i=0;i<60;i++){w=WindowProbe.All().FirstOrDefault(x=>x.ProcessId==fixture.Id);if(w!=null)break;System.Threading.Thread.Sleep(250);}
            if(w==null)throw new Exception("Fixture window enumeration failed");
            var target=new SelectedApplication{Executable=w.Executable,Handle=w.Handle,ProcessId=w.ProcessId,Title=w.Title};
            if(WindowProbe.Find(target).Count!=1)throw new Exception("Explicit application selection failed");
            if(WindowProbe.Find(new SelectedApplication{Executable=@"C:\missing-vendor-app.exe"}).Count!=0)throw new Exception("Unrelated app fallback detected");
            using var capture=new WindowCapture();
            var result=Task.Run(()=>capture.Read(w)).GetAwaiter().GetResult();using(result.Image){if(result.Image.Width!=w.W||result.Image.Height!=w.H)throw new Exception("Window capture size mismatch");result.Image.Save(Path.Combine(folder,"native-selected-window.png"));}
            var retry=WindowProbe.Find(new SelectedApplication{Executable=w.Executable,Handle=-1,Title=w.Title});if(retry.Count!=1)throw new Exception("Window handle recovery failed");
            bool rejected=false;try{var r=Task.Run(()=>capture.Read(w with{Minimized=true})).GetAwaiter().GetResult();r.Image.Dispose();}catch(Exception ex){rejected=ex.Message.StartsWith("WINDOW_MINIMIZED:");}if(!rejected)throw new Exception("Minimized window not rejected");
            string original=Path.Combine(folder,"fixture-shortcut.lnk");
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(original);
            try{link.TargetPath=w.Executable;link.Arguments="--capture-fixture";link.WorkingDirectory=folder;link.Save();}
            finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);}
            target.LaunchPath=original;target.Name="키텍 앱";DesktopLinks.Ensure(new AppConfig{SelectedApp=target});
            dynamic branded=shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"키텍 앱.lnk"));
            try{if((string)branded.Arguments!="--capture-fixture"||!string.Equals((string)branded.TargetPath,w.Executable,StringComparison.OrdinalIgnoreCase))throw new Exception("Original shortcut arguments not preserved");if(!((string)branded.IconLocation).Contains("keytech.ico"))throw new Exception("Keytech icon missing");}
            finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(branded);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);File.Delete(original);}
            using var picker=new AppPickerForm();picker.Show();Application.DoEvents();using(var shot=new Bitmap(picker.Width,picker.Height)){picker.DrawToBitmap(shot,new Rectangle(0,0,shot.Width,shot.Height));shot.Save(Path.Combine(folder,"native-app-picker.png"));}picker.Close();
            return new{shortcutArgumentsPreserved=true,keytechIcon=true,formConstruction=true,windowEnumeration=true,explicitSelection=true,unrelatedAppRejected=true,windowCapture=true,minimizedRejected=true,handleRecovery=true,fixtureOnly=true};
        }finally{if(!fixture.HasExited){fixture.Kill();fixture.WaitForExit(5000);}}
    }
    public static int RunSelfTest()
    {
        Directory.CreateDirectory(AppConfig.Dir);return RuntimeTest(Path.Combine(AppConfig.Dir,"selftest-report.json"));
    }
    public static string ExportDiagnostics()
    {
        string tmp=Path.Combine(Path.GetTempPath(),"rmslink-diag-"+Guid.NewGuid()),output=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"RmsLink-진단-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip");
        Directory.CreateDirectory(tmp);
        try {
            foreach(var file in Directory.GetFiles(AppConfig.Dir,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(7))File.Copy(file,Path.Combine(tmp,Path.GetFileName(file)));
            var cfg=AppConfig.Load();File.WriteAllText(Path.Combine(tmp,"summary.json"),JsonDefaults.Serialize(new {cfg.HotelId,cfg.DeviceId,cfg.Regions,cfg.ServerUrl,cfg.AutoUpdate,version=Updater.Version,ocrLanguages=OcrService.AvailableLanguages()}));
            ZipFile.CreateFromDirectory(tmp,output);return output;
        }finally{Directory.Delete(tmp,true);}
    }
}
