using System.Text.Json;
using System.Text.RegularExpressions;

namespace RmsLink;

public sealed class AdapterProfile
{
    public int Revision { get; set; } = 1;
    public string HotelId { get; set; } = "*";
    public string Name { get; set; } = "공통 키텍 이벤트";
    public string Mode { get; set; } = "events";
    public string RoomPattern { get; set; } = @"(?<![\p{L}\d:])(\d{3,4})\s*호?(?![\d:])";
    public Dictionary<string, string> Aliases { get; set; } = new();
    public Dictionary<string, string> RoomMap { get; set; } = new();
    public int PollMs { get; set; } = 1500;
    public int OcrScale { get; set; } = 3;
    public static readonly HashSet<string> Codes = new(new[] { "DOOR_OPEN", "DOOR_CLOSE", "KEY_IN", "KEY_OUT", "KEY_IN_GUEST", "KEY_OUT_GUEST", "KEY_IN_CLEAN", "KEY_OUT_CLEAN" });
    public void Validate(string hotelId)
    {
        if (HotelId != "*" && HotelId != hotelId) throw new Exception("다른 호텔 프로필입니다.");
        if (Revision < 1 || Mode is not ("events" or "snapshot") || PollMs < 1000 || PollMs > 30000 || OcrScale < 1 || OcrScale > 5 || RoomPattern.Length > 200 || Aliases.Count > 100 || RoomMap.Count > 500) throw new Exception("프로필 범위 오류");
        var rx = new Regex(RoomPattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        if (rx.GetGroupNumbers().Length < 2) throw new Exception("객실 패턴에 첫 번째 캡처 그룹이 필요합니다.");
        foreach (var a in Aliases) if (a.Key.Length < 2 || a.Key.Length > 40 || !Codes.Contains(a.Value)) throw new Exception("이벤트 사전 오류");
        foreach (var r in RoomMap) if (r.Key.Length > 30 || !Regex.IsMatch(r.Value, @"^[\p{L}\d_-]{1,30}$")) throw new Exception("객실 매핑 오류");
    }
    public static AdapterProfile Load(string hotel)
    {
        try {
            var path = Path.Combine(AppConfig.Dir, "profile-" + hotel + ".json");
            if (File.Exists(path)) { var p = JsonSerializer.Deserialize<AdapterProfile>(File.ReadAllText(path), JsonDefaults.Options); p.Validate(hotel); return p; }
        } catch (Exception ex) { Logger.Error("프로필 복구: " + ex.Message); }
        return new AdapterProfile();
    }
}
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
