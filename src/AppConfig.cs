using System.Text.Json;
using System.Text.Json.Serialization;

namespace RmsLink;

public class CaptureRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    [JsonIgnore]
    public Rectangle Rect => new(X, Y, W, H);
}

public class AppConfig
{
    // 보안: DB 접속정보는 공개 바이너리에 넣지 않는다.
    // 우선순위: 환경변수 RMSLINK_DB  >  config.json의 ConnString  >  없음(DB 비활성)
    public string HotelId { get; set; } = "";
    public List<CaptureRegion> Regions { get; set; } = new();
    public int IntervalMs { get; set; } = 1500;
    public int OcrScale { get; set; } = 3;
    public string ConnString { get; set; } = "";

    public string EffectiveConnString()
    {
        var env = Environment.GetEnvironmentVariable("RMSLINK_DB");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return ConnString?.Trim() ?? "";
    }

    public bool DbConfigured => EffectiveConnString().Length > 0;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RmsLink");

    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), ReadOpts);
                if (cfg != null)
                {
                    if (cfg.IntervalMs < 500) cfg.IntervalMs = 500;
                    if (cfg.OcrScale < 1 || cfg.OcrScale > 5) cfg.OcrScale = 3;
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("config load 실패: " + ex.Message);
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, WriteOpts));
    }
}
