using System.Text.Json;
using RmsLink;
using RmsLink.Shared;
using System.Diagnostics;
using System.Security.Cryptography;
if(args.Length==3 && args[0]=="--update-fixture") {
    if(args[2]=="crash")return 3;
    if(args[2]=="healthy")File.WriteAllText(args[1],"ready");
    Thread.Sleep(30000);return 0;
}
if(args.Length==1&&args[0]=="--evaluate") {
    try {
        using var input=JsonDocument.Parse(Console.In.ReadToEnd());var r=input.RootElement;
        var p=JsonSerializer.Deserialize<AdapterProfile>(r.GetProperty("profile"),JsonDefaults.Options);p.Validate(r.GetProperty("hotelId").GetString());
        var cases=r.GetProperty("cases").EnumerateArray().Select(c=>{
            var text=c.GetProperty("line").GetString();var now=DateTimeOffset.Parse(c.GetProperty("now").GetString());
            var result=EventParser.Parse(text,now,p,out var reason);return new {events=result,reason};
        }).ToArray();Console.WriteLine(JsonDefaults.Serialize(new {ok=true,cases}));return 0;
    }catch(Exception ex){Console.WriteLine(JsonDefaults.Serialize(new {ok=false,error=ex.Message}));return 1;}
}
var nowTest=DateTimeOffset.Parse("2026-09-15T12:10:00+09:00");int count=0;
void Test(string line,string code,string room="101",AdapterProfile profile=null){var p=profile??new();var found=EventParser.Parse(line,nowTest,p,out var reason);if(code==null?found.Count!=0:!found.Any(e=>e.Code==code&&e.Room==room))throw new Exception(line+" => "+reason+" "+JsonDefaults.Serialize(found));count++;}
Test("101 문열림 12:00:01","DOOR_OPEN");Test("101 문닫힘 12:00:02","DOOR_CLOSE");Test("101 고객키 삽입 12:01:02","KEY_IN_GUEST");Test("101 청소키 제거 12:01:03","KEY_OUT_CLEAN");
Test("2026-09-15 101 문열림 12:01:03","DOOR_OPEN");Test("101 문열림 99:61:78",null);Test("101 문열림 12:01:65",null);Test("2026-02-30 101 문열림 12:01:03",null);
Test("101 문열림",null);Test("101 문열립 12:01:03",null);Test("101 문닫힘 아님 12:01:03",null);Test("101 키삽입 키제거 12:01:03",null);Test("101 문열림 문닫힘 12:01:03",null);
Test("101 문열림", "DOOR_OPEN",profile:new(){Mode="snapshot"});Test("101 키꽂힘", "KEY_IN",profile:new(){Mode="snapshot"});Test("101 문닫힘 키제거", "KEY_OUT",profile:new(){Mode="snapshot"});
Test("101 102 문열림 12:01:03",null);Test("A101 문열림 12:01:03",null);Test("101 재실 12:01:03",null);Test("2026-09-15 101 문열림",null,profile:new(){Mode="snapshot"});
var old=EventParser.Parse("101 문열림 23:50:00",DateTimeOffset.Parse("2026-01-01T00:10:00+09:00"),new(),out _).Single();if(old.EventDate!=new DateOnly(2025,12,31))throw new Exception("Midnight date");count++;
Test("A101 전원연결 12:01:03","KEY_IN",profile:new(){RoomPattern=@"(A\d{3})",RoomMap=new(){["A101"]="101"},Aliases=new(){["전원연결"]="KEY_IN"}});
void Check(bool condition,string description){if(!condition)throw new Exception(description);count++;}
var known=new AdapterProfile{ExpectedRooms=new(){"101","102"}};
Test("109 문열림 12:01:03",null,profile:known);
Test("A101 전원연결 12:01:03","KEY_IN",profile:new(){RoomPattern=@"(A\d{3})",RoomMap=new(){["A101"]="101"},ExpectedRooms=new(){"101"},Aliases=new(){["전원연결"]="KEY_IN"}});
var read=new ReadingSession();
var first=read.Read(new[]{"101 문열림 12:01:00"},new[]{"101 문열림 12:01:00","102 키삽입 12:01:02"},known,nowTest,"app1");
Check(first.Events.Count==1 && first.Pending==1,"Partial UIA must retain OCR-only room as pending");
var second=read.Read(new[]{"101 문열림 12:01:00"},new[]{"101 문열림 12:01:00","102 키삽입 12:01:02"},known,nowTest.AddSeconds(2),"app1");
Check(second.Events.Count==2 && second.Events.Any(e=>e.Room=="102"),"OCR must supplement partial UIA");
var conflict=read.Read(new[]{"101 문열림 12:01:00"},new[]{"101 문닫힘 12:01:00"},known,nowTest.AddSeconds(4),"app1");
Check(conflict.Events.Count==0 && conflict.Errors.Any(e=>e.StartsWith("READING_CONFLICT")),"Cross-channel disagreement must not become state");
var wrongRoom=read.Read(new[]{"101 문열림 12:01:00"},new[]{"102 문열림 12:01:00"},known,nowTest.AddSeconds(6),"app1");
Check(wrongRoom.Errors.Any(e=>e.StartsWith("READING_CONFLICT")),"Conflicting room identities across channels must block even an allowed OCR number");
read=new();
Check(read.Read(Array.Empty<string>(),new[]{"101 문열림 12:01:00"},known,nowTest,"a").Events.Count==0,"Single OCR observation rejected");
read.BreakContinuity();
Check(read.Read(Array.Empty<string>(),new[]{"101 문열림 12:01:00"},known,nowTest.AddSeconds(2),"a").Events.Count==0,"Capture interruption clears OCR consensus");
Check(read.Read(Array.Empty<string>(),new[]{"101 문열림 12:01:00"},known,nowTest.AddSeconds(4),"b").Events.Count==0,"Application change clears OCR consensus");
Check(read.Read(Array.Empty<string>(),new[]{"102 문열림 12:01:00"},known,nowTest.AddSeconds(6),"b").Events.Count==0,"Unstable OCR room cannot be accepted");
var snapshot=new AdapterProfile{Mode="snapshot",ExpectedRooms=new(){"101","102"}};
read=new();
var initial=read.Read(new[]{"101 문닫힘 키제거"},Array.Empty<string>(),snapshot,nowTest,"a");
Check(initial.Events.Count==2 && initial.MissingRooms.SequenceEqual(new[]{"102"}),"Snapshot coverage reports unseen room");
var unchanged=read.Read(new[]{"101 문닫힘 키제거"},Array.Empty<string>(),snapshot,nowTest.AddSeconds(30),"a");
Check(unchanged.Events.All(e=>e.OccurredAt==nowTest.ToUniversalTime()),"Repeated snapshot must not renew evidence timestamp");
var frozen=read.Read(new[]{"101 문닫힘 키삽입"},Array.Empty<string>(),snapshot,nowTest.AddSeconds(61),"a");
Check(frozen.Events.Count==1 && frozen.Events[0].Code=="KEY_IN" && frozen.UncertainFields.Any(f=>f.Room=="101"&&f.Field=="door"),"One changing field must not refresh unchanged fields");
read.Read(Array.Empty<string>(),Array.Empty<string>(),snapshot,nowTest.AddSeconds(62),"a");
var reappeared=read.Read(new[]{"101 문닫힘 키삽입"},Array.Empty<string>(),snapshot,nowTest.AddSeconds(63),"a");
Check(reappeared.Events.All(e=>!e.Code.StartsWith("DOOR")),"Disappearing and reappearing row must not renew old state");
snapshot.LiveClockPattern=@"갱신 (\d{2}:\d{2}:\d{2})";
read=new();
var clocked=read.Read(new[]{"101 문닫힘","갱신 12:09:50"},Array.Empty<string>(),snapshot,nowTest,"a");
Check(clocked.Events.Count==1 && clocked.Events[0].OccurredAt==nowTest.AddSeconds(-10).ToUniversalTime(),"Configured screen refresh clock supplies evidence time");
var clockFrozen=read.Read(new[]{"101 문닫힘","갱신 12:09:50"},Array.Empty<string>(),snapshot,nowTest.AddSeconds(60),"a");
Check(clockFrozen.Events.Count==0,"Stopped refresh clock expires unchanged snapshot");
var invalidClock=read.Read(new[]{"101 문열림","갱신 12:12:50"},Array.Empty<string>(),snapshot,nowTest.AddSeconds(62),"a");
Check(invalidClock.Events.Count==0,"Future clock rejected");
var hint=new ReadingSession().Read(new[]{"101 문닫힘"},Array.Empty<string>(),known,nowTest,"a");
Check(hint.Events.Count==0 && hint.SuggestedMode=="snapshot","No timestamp only suggests snapshot, never silently changes mode");
var rows=OcrRowLayout.Join(new[]{new ScreenWord("문열림",160,20,50,15),new ScreenWord("101",5,21,30,14),new ScreenWord("12:01:00",300,20,80,15),new ScreenWord("102",5,50,30,15),new ScreenWord("키삽입",160,50,50,15),new ScreenWord("12:01:02",300,50,80,15)});
Check(rows.SequenceEqual(new[]{"101 문열림 12:01:00","102 키삽입 12:01:02"}),"OCR columns reassemble by physical row without joining adjacent rooms");
Check(AccessibleRowLayout.Join("101 문열림 12:01:00",new[]{"101","문열림","12:01:00"})=="101 문열림 12:01:00","UIA row name and cells must not duplicate a room number");
Check(AccessibleRowLayout.Join("101",new[]{"문열림","12:01:00"})=="101 문열림 12:01:00","UIA separate cell values must join into a row");
Check(first.Events.Single().Source=="hybrid","Agreement must retain both reading methods");
Check(second.Events.Single(e=>e.Room=="102").Source=="ocr","OCR supplement must retain OCR provenance");
Check(initial.Events.All(e=>e.Source=="uia"),"Direct readings must retain UIA provenance");
using(var rsa=RSA.Create(2048)) {
    var payload=System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(new UpdateOffer{ExpiresAt=DateTimeOffset.UtcNow.AddHours(1)}));
    var envelope=new SignedEnvelope{Payload=Convert.ToBase64String(payload),Signature=Convert.ToBase64String(rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))};
    Check(Updater.Verify(envelope,rsa.ExportSubjectPublicKeyInfoPem())!=null,"Signed update must verify in actual Windows verifier");
    payload[0]^=1;envelope.Payload=Convert.ToBase64String(payload);
    bool rejected=false;try{Updater.Verify(envelope,rsa.ExportSubjectPublicKeyInfoPem());}catch{rejected=true;}
    Check(rejected,"Tampered update must be rejected by C# verifier");
}
foreach(var mode in new[]{"healthy","crash","timeout","throw"}) {
    var folder=Path.Combine(Path.GetTempPath(),"rmslink-update-test-"+Guid.NewGuid());Directory.CreateDirectory(folder);
    var processes=new List<int>();
    try {
        // Simulate restart after a power loss with current already changed and a stale marker.
        UpdateActivation.WriteCurrent(folder,"0.4.1");
        File.WriteAllText(Path.Combine(folder,"pending.json"),"{\"version\":\"0.4.1\",\"previous\":\"0.4.0\"}");
        File.WriteAllText(Path.Combine(folder,"healthy-0.4.1"),"stale");
        Process LaunchFixture(string version) {
            if(version=="0.4.1"&&mode=="throw")throw new Exception("Cannot execute new binary");
            var psi=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=false};
            if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            psi.ArgumentList.Add("--update-fixture");psi.ArgumentList.Add(Path.Combine(folder,"healthy-"+version));psi.ArgumentList.Add(version=="0.4.0"?"healthy":mode);
            var child=Process.Start(psi);processes.Add(child.Id);return child;
        }
        bool applied=UpdateActivation.Apply(folder,LaunchFixture,TimeSpan.FromSeconds(2));
        Check(applied==(mode=="healthy"),"Update activation result: "+mode);
        Check(UpdateActivation.ReadCurrent(folder)==(applied?"0.4.1":"0.4.0"),"Update pointer/rollback: "+mode);
        if(!applied){var wait=Stopwatch.StartNew();while(!File.Exists(Path.Combine(folder,"healthy-0.4.0"))&&wait.Elapsed<TimeSpan.FromSeconds(5))Thread.Sleep(50);Check(File.Exists(Path.Combine(folder,"healthy-0.4.0")),"Previous process actually started: "+mode);}
        Check(!File.Exists(Path.Combine(folder,"pending.json")),"Pending update cleared: "+mode);
        Check(File.Exists(Path.Combine(folder,"failed-update.json"))!=applied,"Failed version quarantine: "+mode);
    } finally {
        foreach(var processId in processes)try{using var process=Process.GetProcessById(processId);if(!process.HasExited){process.Kill(true);process.WaitForExit(5000);}}catch{}
        Directory.Delete(folder,true);
    }
}
Console.WriteLine(JsonDefaults.Serialize(new {passed=true,tests=count}));return 0;
namespace RmsLink {public class AppConfig{public static string Dir=>Path.GetTempPath();public static string InstallDir=>Path.GetTempPath();}public static class Logger{public static void Error(string s)=>Console.Error.WriteLine(s);}}
