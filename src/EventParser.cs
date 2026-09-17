using System.Globalization;
using System.Text.RegularExpressions;
namespace RmsLink;
public sealed record ParsedEvent(string Room, string Code, string EventRaw, string EventTime, DateOnly EventDate, string RawLine, bool Fuzzy)
{
    public string Kind { get; init; } = "event";
    public string Source { get; init; } = "unknown";
    public string DedupKey => $"{Room}|{Code}|{EventTime}|{EventDate:yyyyMMdd}|{Kind}";
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset OccurredAt { get; init; }
}
public static class EventParser
{
    public static readonly Dictionary<string,string> Common = new() {
        ["출입문열림"]="DOOR_OPEN", ["출입문닫힘"]="DOOR_CLOSE", ["문열림"]="DOOR_OPEN", ["문닫힘"]="DOOR_CLOSE",
        ["고객키삽입"]="KEY_IN_GUEST", ["손님키삽입"]="KEY_IN_GUEST", ["고객키제거"]="KEY_OUT_GUEST", ["손님키제거"]="KEY_OUT_GUEST",
        ["청소키삽입"]="KEY_IN_CLEAN", ["청소키제거"]="KEY_OUT_CLEAN", ["키삽입"]="KEY_IN", ["키제거"]="KEY_OUT",
        ["키꽂힘"]="KEY_IN", ["키빠짐"]="KEY_OUT", ["문열림상태"]="DOOR_OPEN", ["문닫힘상태"]="DOOR_CLOSE"
    };
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    static Match Match(string text,string pattern) => Regex.Match(text,pattern,RegexOptions.None,Timeout);
    public static ParsedEvent TryParse(string line, DateTime now, out string reason) => Parse(line,new DateTimeOffset(now),new AdapterProfile(),out reason).FirstOrDefault();
    public static List<ParsedEvent> Parse(string line, DateTimeOffset now, AdapterProfile profile, out string reason)
    {
        reason="";
        var result=new List<ParsedEvent>();
        if (string.IsNullOrWhiteSpace(line) || line.Length>1000) { reason="빈 줄 또는 길이 초과"; return result; }
        string work=line.Normalize(); var occurred=now; var local=now;
        var date=Match(work,@"(?<!\d)(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})(?!\d)");
        var shortDate=date.Success?Match("", "x"):Match(work,@"(?<!\d)(\d{1,2})[-/.](\d{1,2})(?!\d)");
        if(date.Success) work=work.Remove(date.Index,date.Length);
        else if(shortDate.Success) work=work.Remove(shortDate.Index,shortDate.Length);
        var time=Match(work,@"(?<!\d)(\d{1,2})\s*[:;]\s*(\d{2})(?:\s*[:;]\s*(\d{2}))?(?!\d)");
        if(profile.Mode=="events" && !time.Success){reason="시각 없음: 이벤트 로그 영역 확인 필요";return result;}
        if(time.Success) {
            try {
                int hh=int.Parse(time.Groups[1].Value),mm=int.Parse(time.Groups[2].Value),ss=time.Groups[3].Success?int.Parse(time.Groups[3].Value):0;
                int yy=local.Year,mo=local.Month,dd=local.Day;
                if(date.Success){yy=int.Parse(date.Groups[1].Value);mo=int.Parse(date.Groups[2].Value);dd=int.Parse(date.Groups[3].Value);}
                else if(shortDate.Success){mo=int.Parse(shortDate.Groups[1].Value);dd=int.Parse(shortDate.Groups[2].Value);if(new DateTime(yy,mo,dd)>local.Date.AddDays(1)) yy--;}
                occurred=new DateTimeOffset(yy,mo,dd,hh,mm,ss,local.Offset);
                if(!date.Success && !shortDate.Success && occurred>now.AddHours(2)) occurred=occurred.AddDays(-1);
                if(occurred>now.AddMinutes(2)){reason="미래 시각: Windows 시계 확인";return result;}
                work=work.Remove(time.Index,time.Length);
            } catch {reason="날짜 또는 시각 범위 오류";return result;}
        } else if(date.Success || shortDate.Success) { reason="시각 없는 과거 날짜는 현재 상태로 사용할 수 없음";return result; }
        var roomMatch=Match(work,profile.RoomPattern);
        if(!roomMatch.Success || roomMatch.Groups.Count<2){reason="객실번호 없음";return result;}
        if(Regex.Matches(work,profile.RoomPattern,RegexOptions.None,Timeout).Count>1){reason="여러 객실번호가 한 줄에 있음: 로그 열 분리 필요";return result;}
        string room=roomMatch.Groups[1].Value;
        if(profile.RoomMap.TryGetValue(room,out var mapped)) room=mapped;
        if(!Regex.IsMatch(room,@"^[\p{L}\d_-]{1,30}$")){reason="객실번호 범위 오류";return result;}
        if(profile.ExpectedRooms.Count>0 && !profile.ExpectedRooms.Contains(room)){reason="등록되지 않은 객실번호: "+room;return result;}
        work=work.Remove(roomMatch.Index,roomMatch.Length);
        var text=Regex.Replace(work,@"[\s\p{P}]", "",RegexOptions.None,Timeout);
        // No fuzzy auto-accept: 삽입/제거 and 열림/닫힘 are safety-critical opposites.
        if(Regex.IsMatch(text,@"아님|않음|불명|미확인|오류|실패|unknown|error",RegexOptions.IgnoreCase,Timeout)){reason="부정 또는 오류 상태";return result;}
        var vocab=new Dictionary<string,string>(Common);
        foreach(var a in profile.Aliases) vocab[a.Key]=a.Value;
        var matches=new List<(string Key,string Code)>();
        foreach(var a in vocab.OrderByDescending(x=>x.Key.Length)) {
            string key=Regex.Replace(a.Key,@"[\s\p{P}]", "",RegexOptions.None,Timeout);
            if(!text.Contains(key,StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add((a.Key,a.Value)); text=Regex.Replace(text,Regex.Escape(key),"",RegexOptions.IgnoreCase,Timeout);
        }
        foreach(var group in matches.GroupBy(x=>x.Code.StartsWith("DOOR")?"door":"key")) {
            var codes=group.Select(x=>x.Code).Distinct().ToArray();
            if(codes.Length>1){reason="한 줄에 상충 상태: 영역/열 분리 필요";return new();}
            var item=group.First();
            result.Add(new ParsedEvent(room,item.Code,item.Key,occurred.ToString("HH:mm:ss"),DateOnly.FromDateTime(occurred.Date),line,false){Kind=profile.Mode=="snapshot"?"snapshot":"event",ObservedAt=now.ToUniversalTime(),OccurredAt=(profile.Mode=="snapshot"?now:occurred).ToUniversalTime()});
        }
        if(result.Count==0) reason="어휘 불일치: 호텔별 사전 분석 필요";
        return result;
    }
}
