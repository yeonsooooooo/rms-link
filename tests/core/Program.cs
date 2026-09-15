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
Console.WriteLine(JsonDefaults.Serialize(new {passed=true,tests=count}));return 0;
namespace RmsLink {public static class AppConfig{public static string Dir=>Path.GetTempPath();}public static class Logger{public static void Error(string s)=>Console.Error.WriteLine(s);}}
