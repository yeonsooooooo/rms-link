using System.Text.Json;
using RmsLink;
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
Console.WriteLine(JsonDefaults.Serialize(new {passed=true,tests=count}));return 0;
namespace RmsLink {public static class AppConfig{public static string Dir=>Path.GetTempPath();}public static class Logger{public static void Error(string s)=>Console.Error.WriteLine(s);}}
