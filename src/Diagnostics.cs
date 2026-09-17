using RmsLink.Shared;
using System.Text.Json;
using System.IO.Compression;
namespace RmsLink;
public static class Diagnostics
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
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
        form.Controls.Add(new Label{Text="101 DOOR OPEN",AutoSize=true,Location=new(40,50),Font=new("Arial",22)});
        form.Controls.Add(new Label{Text="102 KEY IN",AutoSize=true,Location=new(40,120),Font=new("Arial",22)});
        form.Controls.Add(new TextBox{Text="103 DOOR OPEN",ReadOnly=true,Multiline=true,Location=new(40,200),Size=new(550,65),Font=new("Arial",22)});
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
            var profile=new AdapterProfile{Mode="snapshot",ExpectedRooms=new(){"101","102","103"},Aliases=new(){["DOOR OPEN"]="DOOR_OPEN",["KEY IN"]="KEY_IN"}};
            var uia=Task.Run(()=>WindowProbe.ReadAccessibleRows(w));
            if(!uia.Wait(5000))throw new Exception("Fixture accessibility timeout");
            if(new ReadingSession().Read(uia.Result,Array.Empty<string>(),profile,DateTimeOffset.Now,"fixture").Events.Count!=3)throw new Exception("Native accessibility rows not parsed");
            var ocr=OcrService.Create()??throw new Exception("Windows OCR engine unavailable");
            var reader=new ReadingSession();
            for(int i=0;i<2;i++) {
                var frame=Task.Run(()=>capture.Read(w)).GetAwaiter().GetResult();
                using(frame.Image) {
                    var lines=Task.Run(()=>ocr.ReadLinesAsync(frame.Image,2)).GetAwaiter().GetResult();
                    var parsed=reader.Read(Array.Empty<string>(),lines,profile,DateTimeOffset.Now,"fixture");
                    if(parsed.Events.Count!=(i==0?0:3))throw new Exception("Native OCR consensus failed: "+JsonDefaults.Serialize(lines));
                }
            }
            // A real overlapping window exercises PrintWindow, not screen pixels from another app.
            using(var cover=new Form{Text="RmsLink occlusion fixture",StartPosition=FormStartPosition.Manual,Bounds=new(w.X,w.Y,w.W,w.H),BackColor=Color.Magenta,TopMost=true}) {
                cover.Show();Application.DoEvents();System.Threading.Thread.Sleep(200);
                var behind=Task.Run(()=>capture.Read(w)).GetAwaiter().GetResult();
                using(behind.Image) {
                    if(behind.Method!="print-window")throw new Exception("Occluded fixture did not use PrintWindow");
                    var lines=Task.Run(()=>ocr.ReadLinesAsync(behind.Image,2)).GetAwaiter().GetResult();
                    if(!lines.Any(l=>l.Contains("101")))throw new Exception("Occluded window content missing");
                }
                cover.Close();
            }
            var retry=WindowProbe.Find(new SelectedApplication{Executable=w.Executable,Handle=-1,Title=w.Title});if(retry.Count!=1)throw new Exception("Window handle recovery failed");
            ShowWindow(new(w.Handle),6);System.Threading.Thread.Sleep(300);
            var minimized=WindowProbe.Find(target).Single();
            if(!minimized.Minimized)throw new Exception("Actual fixture window not minimized");
            bool rejected=false;try{var r=Task.Run(()=>capture.Read(minimized)).GetAwaiter().GetResult();r.Image.Dispose();}catch(Exception ex){rejected=ex.Message.StartsWith("WINDOW_MINIMIZED:");}if(!rejected)throw new Exception("Minimized window not rejected");
            WindowProbe.Restore(minimized);System.Threading.Thread.Sleep(500);
            if(WindowProbe.Find(target).Single().Minimized)throw new Exception("Minimized fixture restoration failed");
            string original=Path.Combine(folder,"fixture-shortcut.lnk");
            ShortcutFile.Create(original,w.Executable,"검증용 키텍",Path.Combine(AppContext.BaseDirectory,"assets","keytech.ico"),"--capture-fixture",folder);
            target.LaunchPath=original;target.Name="키텍 앱";DesktopLinks.Ensure(new AppConfig{SelectedApp=target});
            var branded=ShortcutFile.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"키텍 앱.lnk"));
            if(branded.Arguments!="--capture-fixture"||!string.Equals(branded.Target,w.Executable,StringComparison.OrdinalIgnoreCase))throw new Exception("Original shortcut arguments not preserved");
            if(!branded.Icon.Contains("keytech.ico"))throw new Exception("Keytech icon missing");
            File.Delete(original);
            using var picker=new AppPickerForm();picker.Show();Application.DoEvents();using(var shot=new Bitmap(picker.Width,picker.Height)){picker.DrawToBitmap(shot,new Rectangle(0,0,shot.Width,shot.Height));shot.Save(Path.Combine(folder,"native-app-picker.png"));}picker.Close();
            return new{shortcutArgumentsPreserved=true,keytechIcon=true,formConstruction=true,windowEnumeration=true,explicitSelection=true,unrelatedAppRejected=true,windowCapture=true,minimizedRejected=true,minimizedRestored=true,accessibilityParsed=true,visibleTextPattern=true,ocrConsensus=true,occludedCapture=true,handleRecovery=true,fixtureOnly=true};
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
