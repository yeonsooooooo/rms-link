using System.Text.RegularExpressions;

namespace RmsLink;

public sealed record ReadLine(string Text, string Source, string Reason, List<ParsedEvent> Events);
public sealed record UncertainField(string Room, string Field);
public sealed class ReadingResult
{
    public List<ParsedEvent> Events { get; } = new();
    public List<ReadLine> Lines { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<UncertainField> UncertainFields { get; } = new();
    public List<string> MissingRooms { get; set; } = new();
    public int Pending { get; set; }
    public string SuggestedMode { get; set; } = "events";
    public DateTimeOffset? SnapshotEvidenceAt { get; set; }
}

/// <summary>Pure screen-reading policy, shared by Windows and regression tests.</summary>
public sealed class ReadingSession
{
    HashSet<string> previousOcr = new();
    string context = "";
    readonly Dictionary<string,(string Code, DateTimeOffset At)> snapshots = new();
    DateTimeOffset? previousAt;

    public void BreakContinuity() { previousOcr.Clear(); previousAt = null; }
    static string Field(ParsedEvent e) => e.Code.StartsWith("DOOR") ? "door" : "key";
    static string EvidenceKey(ParsedEvent e) => $"{e.Room}|{e.Code}|{e.Kind}|{(e.Kind=="event"?e.OccurredAt.ToString("O"):"")}";

    public ReadingResult Read(IReadOnlyList<string> uia, IReadOnlyList<string> ocr,
        AdapterProfile profile, DateTimeOffset now, string identity)
    {
        var nextContext = identity + "|" + JsonDefaults.Serialize(profile);
        if(context != nextContext) {
            context = nextContext; snapshots.Clear(); BreakContinuity();
        }
        if(previousAt.HasValue && now-previousAt.Value > TimeSpan.FromMilliseconds(Math.Max(15000,profile.PollMs*3))) BreakContinuity();
        var result = new ReadingResult { SuggestedMode = profile.Mode };
        var candidates = new List<(ParsedEvent Event, string Source)>();
        var currentOcr = new HashSet<string>();
        var rejectedRooms = new HashSet<string>();
        bool snapshotCandidate = false;
        foreach(var source in new[]{(Lines:uia,Source:"uia"),(Lines:ocr,Source:"ocr")})
        foreach(var text in source.Lines.Take(300)) {
            var events = EventParser.Parse(text,now,profile,out var reason,out var uncertainRooms);
            rejectedRooms.UnionWith(uncertainRooms);
            result.Lines.Add(new(text,source.Source,reason,events));
            candidates.AddRange(events.Select(e=>(e,source.Source)));
            if(source.Source=="ocr") foreach(var e in events) currentOcr.Add(EvidenceKey(e));
            if(reason.StartsWith("등록되지 않은 객실번호")) result.Warnings.Add("ROOM_NOT_ALLOWED: "+reason);
            if(uncertainRooms.Count>0) result.Warnings.Add("LAYOUT_AMBIGUOUS: "+reason);
            if(profile.Mode=="events" && reason.StartsWith("시각 없음")) {
                var probe = new AdapterProfile { Mode="snapshot", RoomPattern=profile.RoomPattern, RoomMap=profile.RoomMap, Aliases=profile.Aliases, ExpectedRooms=profile.ExpectedRooms };
                if(EventParser.Parse(text,now,probe,out _).Count>0) snapshotCandidate=true;
            }
        }
        var conflictedFields=new HashSet<string>();
        foreach(var room in rejectedRooms)
        foreach(var field in new[]{"door","key"})conflictedFields.Add(room+"|"+field);
        foreach(var pair in candidates.Where(c=>c.Event.Kind=="event").GroupBy(c=>$"{c.Event.Code}|{c.Event.OccurredAt:O}")) {
            var accessibleRooms=pair.Where(c=>c.Source=="uia").Select(c=>c.Event.Room).Distinct().ToArray();
            var imageRooms=pair.Where(c=>c.Source=="ocr").Select(c=>c.Event.Room).Distinct().ToArray();
            if(accessibleRooms.Length==1 && imageRooms.Length==1 && accessibleRooms[0]!=imageRooms[0]) {
                result.Warnings.Add($"READING_CONFLICT: 동일 상태·시각의 객실 번호가 접근성({accessibleRooms[0]})과 OCR({imageRooms[0]})에서 다릅니다");
                foreach(var candidate in pair)conflictedFields.Add(candidate.Event.Room+"|"+Field(candidate.Event));
            }
        }
        // Identify all conflicts before accepting any timestamp for the affected field.
        var groups=candidates.GroupBy(c=>$"{c.Event.Room}|{Field(c.Event)}|{c.Event.Kind}|{(c.Event.Kind=="event"?c.Event.OccurredAt.ToString("O"):"")}").ToList();
        foreach(var group in groups) {
            var values = group.Select(c=>c.Event.Code).Distinct().ToArray();
            var chosen = group.First();
            if(values.Length>1) {
                result.Warnings.Add($"READING_CONFLICT: {chosen.Event.Room} {Field(chosen.Event)} 판독 불일치");
                conflictedFields.Add(chosen.Event.Room+"|"+Field(chosen.Event));
            }
        }
        foreach(var group in groups) {
            var chosen=group.First();
            if(conflictedFields.Contains(chosen.Event.Room+"|"+Field(chosen.Event)))continue;
            var accessible = group.FirstOrDefault(c=>c.Source=="uia");
            if(accessible.Event!=null) chosen=accessible;
            else if(!previousOcr.Contains(EvidenceKey(chosen.Event))) {
                result.Pending++;
                result.UncertainFields.Add(new(chosen.Event.Room,Field(chosen.Event)));
                continue;
            }
            result.Events.Add(chosen.Event with { Source = accessible.Event!=null ? (group.Any(c=>c.Source=="ocr") ? "hybrid" : "uia") : "ocr" });
        }
        // Quarantine the affected room/field, including other timestamps in this frame.
        // A malformed unrelated row must not suppress valid rooms across the whole hotel.
        result.Events.RemoveAll(e=>conflictedFields.Contains(e.Room+"|"+Field(e)));
        foreach(var key in conflictedFields) {
            int split=key.LastIndexOf('|');result.UncertainFields.Add(new(key[..split],key[(split+1)..]));
        }
        currentOcr.ExceptWith(candidates.Where(c=>conflictedFields.Contains(c.Event.Room+"|"+Field(c.Event))).Select(c=>EvidenceKey(c.Event)));
        previousOcr=currentOcr; previousAt=now;
        if(result.Pending>0) result.Warnings.Add($"OCR_CONFIRMING: {result.Pending}개 상태를 다음 화면과 대조 중");
        if(snapshotCandidate) {
            result.SuggestedMode="snapshot";
            result.Warnings.Add("MODE_CONFIRMATION_REQUIRED: 시각 없는 상태 문구가 있습니다. 현재 상태표인지 확인하고 화면 설정에서 선택하세요");
        }
        var unmatched=result.Lines.Count(l=>l.Reason.StartsWith("어휘 불일치"));
        if(unmatched>0) result.Warnings.Add($"UNMATCHED_ROWS: {unmatched}개 행의 상태 문구를 해석하지 못했습니다. 원문과 실제 상태를 대조하세요");
        if(profile.ExpectedRooms.Count==0) result.Warnings.Add("ROOM_LIST_UNCONFIRMED: 화면 설정에서 실제 객실 목록을 등록하면 번호 오인식과 누락을 확인할 수 있습니다");
        var observed = result.Events.Select(e=>e.Room).ToHashSet();
        result.MissingRooms=profile.ExpectedRooms.Where(r=>!observed.Contains(r)).ToList();
        if(profile.Mode=="snapshot") {
            // Do not renew an unchanged state table just because it was captured again.
            var clock = profile.LiveClockPattern.Length>0 ? ReadClock(uia.Concat(ocr),profile.LiveClockPattern,now) : null;
            for(int i=result.Events.Count-1;i>=0;i--) {
                var e=result.Events[i]; var key=e.Room+"|"+Field(e);
                if(!snapshots.TryGetValue(key,out var previous) || previous.Code!=e.Code) snapshots[key]=(e.Code,now);
                var evidenceAt=profile.LiveClockPattern.Length>0 ? clock : snapshots[key].At;
                if(!evidenceAt.HasValue || now-evidenceAt.Value>=TimeSpan.FromSeconds(60)) {
                    result.Events.RemoveAt(i); result.UncertainFields.Add(new(e.Room,Field(e)));
                    result.Warnings.Add("SCREEN_FRESHNESS_UNCONFIRMED: 상태표의 갱신을 확인할 수 없어 해당 상태를 미확인으로 표시합니다");
                } else {
                    result.Events[i]=e with {OccurredAt=evidenceAt.Value.ToUniversalTime()};
                    if(!result.SnapshotEvidenceAt.HasValue || evidenceAt<result.SnapshotEvidenceAt) result.SnapshotEvidenceAt=evidenceAt;
                }
            }
            var currentRooms=result.Events.Select(e=>e.Room).ToHashSet();
            result.MissingRooms=profile.ExpectedRooms.Where(r=>!currentRooms.Contains(r)).ToList();
            if(result.MissingRooms.Count>0) result.Warnings.Add($"ROOMS_NOT_VISIBLE: {result.MissingRooms.Count}개 객실이 이번 화면에서 확인되지 않았습니다");
        }
        return result;
    }

    static DateTimeOffset? ReadClock(IEnumerable<string> lines,string pattern,DateTimeOffset now)
    {
        var clocks=new HashSet<DateTimeOffset>();
        foreach(var line in lines) {
            var match=Regex.Match(line,pattern,RegexOptions.None,TimeSpan.FromMilliseconds(50));
            if(!match.Success || !TimeOnly.TryParseExact(match.Groups[1].Value,"HH:mm:ss",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var time)) continue;
            var at=new DateTimeOffset(now.Year,now.Month,now.Day,time.Hour,time.Minute,time.Second,now.Offset);
            if(at>now.AddHours(12)) at=at.AddDays(-1);
            if(at<=now && now-at<TimeSpan.FromSeconds(60)) clocks.Add(at);
        }
        // OCR and accessibility can be sampled on adjacent seconds; reject larger disagreement.
        return clocks.Count>0 && clocks.Max()-clocks.Min()<=TimeSpan.FromSeconds(3) ? clocks.Min() : null;
    }
}
