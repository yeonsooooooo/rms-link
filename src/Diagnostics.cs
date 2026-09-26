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
    public static int AccessibilityFixture()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var form=new Form{Text="RmsLink nested event table fixture",ClientSize=new(700,380),StartPosition=FormStartPosition.Manual,Location=new(30,30)};
        Control host=form;
        for(int i=0;i<10;i++){var panel=new Panel{Dock=DockStyle.Fill};host.Controls.Add(panel);host=panel;}
        host.Controls.Add(new Label{Text="104",Location=new(20,20),Size=new(60,25)});
        host.Controls.Add(new Label{Text="DOOR OPEN",Location=new(120,20),Size=new(140,25)});
        host.Controls.Add(new Label{Text="12:01:00",Location=new(300,20),Size=new(120,25)});
        var grid=new DataGridView{Location=new(20,90),Size=new(620,130),AllowUserToAddRows=false,ReadOnly=true,RowHeadersVisible=false};
        grid.Columns.Add("room","객실");grid.Columns.Add("event","상태");grid.Columns.Add("time","시각");
        grid.Rows.Add("105","KEY IN","12:01:01");host.Controls.Add(grid);
        // A password must never become event evidence.
        host.Controls.Add(new TextBox{Text="106 DOOR OPEN 12:01:02",UseSystemPasswordChar=true,Location=new(20,270),Width=600});
        using var timer=new System.Windows.Forms.Timer{Interval=90000};timer.Tick+=(_,_)=>form.Close();timer.Start();Application.Run(form);return 0;
    }
    static void NativeAccessibleTableTest()
    {
        using var fixture=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath,"--accessibility-fixture"){UseShellExecute=false});
        try {
            WindowCandidate w=null;for(int i=0;i<60;i++){w=WindowProbe.All().FirstOrDefault(x=>x.ProcessId==fixture.Id);if(w!=null)break;Thread.Sleep(250);}
            if(w==null)throw new Exception("Nested table fixture window missing");
            var profile=new AdapterProfile{Aliases=new(){["DOOR OPEN"]="DOOR_OPEN",["KEY IN"]="KEY_IN"}};
            var now=DateTimeOffset.Parse("2026-09-26T12:02:00+09:00");
            var task=Task.Run(()=>WindowProbe.ReadAccessible(w));
            if(!task.Wait(5000))throw new Exception("Nested table accessibility timeout");
            var events=new ReadingSession().Read(task.Result.Lines,Array.Empty<string>(),profile,now,"table").Events;
            if(!events.Select(e=>e.Room).Order().SequenceEqual(new[]{"104","105"}))throw new Exception("Nested cells/ValuePattern rows missing or password leaked: "+JsonDefaults.Serialize(task.Result));
            var roi=Task.Run(()=>WindowProbe.ReadAccessible(w,new Rectangle(0,0,w.W,60)));
            if(!roi.Wait(5000))throw new Exception("Region accessibility timeout");
            var regionEvents=new ReadingSession().Read(roi.Result.Lines,Array.Empty<string>(),profile,now,"roi").Events;
            if(regionEvents.Count!=1 || regionEvents[0].Room!="104")throw new Exception("Region must read only its complete event row: "+JsonDefaults.Serialize(roi.Result));
        }finally {if(!fixture.HasExited){fixture.Kill();fixture.WaitForExit(5000);}}
    }
    static object NativeCaptureTest(string folder)
    {
        NativeAccessibleTableTest();
        using(var setup=new SetupForm(new())){
            setup.ClientSize=new(640,400);setup.Show();Application.DoEvents();
            if(!setup.VerticalScroll.Visible)throw new Exception("Small-display setup must scroll to the connection button");
            var connect=setup.Controls.OfType<Button>().Single(b=>b.Text.StartsWith("3."));
            setup.ScrollControlIntoView(connect);Application.DoEvents();
            if(!setup.ClientRectangle.Contains(connect.Bounds))throw new Exception("Connection button is unreachable on a small display");
            setup.Close();
        }
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
            return new{nestedAccessibleTable=true,regionAccessibility=true,passwordExcluded=true,shortcutArgumentsPreserved=true,keytechIcon=true,formConstruction=true,windowEnumeration=true,explicitSelection=true,unrelatedAppRejected=true,windowCapture=true,minimizedRejected=true,minimizedRestored=true,accessibilityParsed=true,visibleTextPattern=true,ocrConsensus=true,occludedCapture=true,handleRecovery=true,fixtureOnly=true};
        }finally{if(!fixture.HasExited){fixture.Kill();fixture.WaitForExit(5000);}}
    }
    public static int RunSelfTest()
    {
        Directory.CreateDirectory(AppConfig.Dir);return RuntimeTest(Path.Combine(AppConfig.Dir,"selftest-report.json"));
    }
    public static string ExportDiagnostics()
    {
        var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try {return SupportBundle.Export(AppConfig.InstallDir,AppConfig.Dir,desktop);}
        catch {return SupportBundle.Export(AppConfig.InstallDir,AppConfig.Dir,Path.GetTempPath());}
    }
}
