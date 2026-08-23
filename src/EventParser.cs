using System.Text.RegularExpressions;

namespace RmsLink;

public sealed record ParsedEvent(
    string Room,
    string Code,
    string EventRaw,
    string EventTime,
    DateOnly EventDate,
    string RawLine,
    bool Fuzzy)
{
    public string DedupKey => $"{Room}|{EventRaw}|{EventTime}|{EventDate:yyyyMMdd}";
}

public static class EventParser
{
    // 6개 RMS 화면(A/S Call Center, UBSYS, RMS7, ROMASYS, SeeReal SMP, MIRAE-TCS)에서
    // 실제 관찰된 이벤트 어휘. 긴 키워드 우선 매칭.
    private static readonly (string Key, string Code)[] Vocab = new[]
    {
        ("출입문열림", "DOOR_OPEN"),
        ("출입문닫힘", "DOOR_CLOSE"),
        ("고객키삽입", "KEY_IN_GUEST"),
        ("고객키제거", "KEY_OUT_GUEST"),
        ("손님키삽입", "KEY_IN_GUEST"),
        ("손님키제거", "KEY_OUT_GUEST"),
        ("청소키삽입", "KEY_IN_CLEAN"),
        ("청소키제거", "KEY_OUT_CLEAN"),
        ("객실입장",   "CHECKIN"),
        ("공실전환",   "VACANT"),
        ("외출복귀",   "OUT_RETURN"),
        ("청소시작",   "CLEAN_START"),
        ("청소완료",   "CLEAN_DONE"),
        ("청소지시",   "CLEAN_ORDER"),
        ("체크아웃",   "CHECKOUT"),
        ("고객없음",   "NO_GUEST"),
        ("문열림",     "DOOR_OPEN"),
        ("문닫힘",     "DOOR_CLOSE"),
        ("키삽입",     "KEY_IN"),
        ("키제거",     "KEY_OUT"),
        ("체크인",     "CHECKIN"),
        ("청소중",     "CLEAN_START"),
        ("열림",       "DOOR_OPEN"),
        ("닫힘",       "DOOR_CLOSE"),
        ("외출",       "OUT"),
        ("공실",       "VACANT"),
        ("퇴실",       "CHECKOUT"),
        ("입실",       "CHECKIN"),
        ("숙박",       "STAY"),
        ("대실",       "DAESIL"),
        ("장기",       "LONG_STAY"),
        ("예약",       "RESERVE"),
    };

    private static readonly Regex TimeRx =
        new(@"(?<!\d)(\d{1,2})\s*[:;]\s*(\d{2})(?:\s*[:;]\s*(\d{2}))?(?!\d)", RegexOptions.Compiled);

    private static readonly Regex DateRx =
        new(@"(?<!\d)(\d{1,2})[-/.](\d{1,2})(?![\d/.\-])", RegexOptions.Compiled);

    private static readonly Regex RoomRx =
        new(@"^(\d{3,4})호?$", RegexOptions.Compiled);

    private static readonly Regex KoreanOnlyRx = new(@"[^가-힣]", RegexOptions.Compiled);

    /// <summary>한 줄을 파싱. 실패 시 null 반환, reason에 사유.</summary>
    public static ParsedEvent TryParse(string line, DateTime now, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(line)) { reason = "빈 줄"; return null; }

        var work = line;

        // 1) 시각 추출 (가장 오른쪽 매치 사용 - 로그의 시각 컬럼은 보통 우측)
        string eventTime = "";
        var timeMatches = TimeRx.Matches(work);
        if (timeMatches.Count > 0)
        {
            var m = timeMatches[^1];
            int hh = int.Parse(m.Groups[1].Value);
            int mm = int.Parse(m.Groups[2].Value);
            if (hh <= 23 && mm <= 59)
            {
                string ss = m.Groups[3].Success ? m.Groups[3].Value : "";
                eventTime = m.Groups[3].Success ? $"{hh:00}:{mm:00}:{ss}" : $"{hh:00}:{mm:00}";
                work = work.Remove(m.Index, m.Length);
            }
        }
        if (eventTime.Length == 0) { reason = "시각 없음"; return null; }

        // 2) 날짜(월-일) 추출 (있으면)
        int month = 0, day = 0;
        var dm = DateRx.Match(work);
        if (dm.Success)
        {
            int mo = int.Parse(dm.Groups[1].Value);
            int dd = int.Parse(dm.Groups[2].Value);
            if (mo is >= 1 and <= 12 && dd is >= 1 and <= 31)
            {
                month = mo; day = dd;
                work = work.Remove(dm.Index, dm.Length);
            }
        }

        // 3) 객실번호 추출 (3~4자리 숫자 토큰, '호' 접미 허용)
        string room = "";
        foreach (var tok in work.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rm = RoomRx.Match(tok.Trim());
            if (rm.Success) { room = rm.Groups[1].Value; break; }
        }
        if (room.Length == 0) { reason = "객실번호 없음"; return null; }

        // 4) 이벤트 어휘 매칭 (한글만 남긴 문자열에 대해 긴 키워드부터)
        string korean = KoreanOnlyRx.Replace(work, "");
        if (korean.Length == 0) { reason = "이벤트 텍스트 없음"; return null; }

        foreach (var (key, code) in Vocab)
        {
            if (korean.Contains(key))
                return new ParsedEvent(room, code, key, eventTime,
                    ResolveDate(now, month, day, eventTime), line.Trim(), false);
        }

        // 5) 직접 매칭 실패 시 1글자 오독 허용 (키워드 길이 3자 이상만)
        foreach (var (key, code) in Vocab)
        {
            if (key.Length < 3) continue;
            if (ContainsWithOneEdit(korean, key))
                return new ParsedEvent(room, code, key, eventTime,
                    ResolveDate(now, month, day, eventTime), line.Trim(), true);
        }

        reason = $"어휘 불일치: '{korean}'";
        return null;
    }

    private static DateOnly ResolveDate(DateTime now, int month, int day, string eventTime)
    {
        if (month > 0)
        {
            int year = now.Year;
            try
            {
                var d = new DateOnly(year, month, day);
                // 미래 날짜로 나오면 작년 로그로 간주 (연말/연초 경계)
                if (d > DateOnly.FromDateTime(now).AddDays(1)) d = new DateOnly(year - 1, month, day);
                return d;
            }
            catch { /* 잘못된 날짜면 오늘로 */ }
        }
        var today = DateOnly.FromDateTime(now);
        // 자정 직후 어제 로그(시각이 현재보다 2시간 이상 미래)면 어제 날짜로
        if (TimeOnly.TryParse(eventTime.Length == 5 ? eventTime + ":00" : eventTime, out var t))
        {
            if (t.ToTimeSpan() > now.TimeOfDay + TimeSpan.FromHours(2)) return today.AddDays(-1);
        }
        return today;
    }

    /// <summary>haystack 안에 needle과 편집거리 1 이내인 동일 길이 부분문자열이 있는지</summary>
    private static bool ContainsWithOneEdit(string haystack, string needle)
    {
        int n = needle.Length;
        if (haystack.Length < n) return false;
        for (int i = 0; i + n <= haystack.Length; i++)
        {
            int diff = 0;
            for (int j = 0; j < n && diff <= 1; j++)
                if (haystack[i + j] != needle[j]) diff++;
            if (diff <= 1) return true;
        }
        return false;
    }
}
