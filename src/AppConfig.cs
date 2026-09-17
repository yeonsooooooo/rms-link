using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
namespace RmsLink;
public class CaptureRegion
{
    public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; }
    [JsonIgnore] public Rectangle Rect => new(X,Y,W,H);
}
public class SelectedApplication
{
    public string Vendor {get;set;}="기타 / 모름";
    public string Name {get;set;}=""; public string Executable {get;set;}=""; public string LaunchPath {get;set;}=""; public string Title {get;set;}=""; public long Handle {get;set;} public int ProcessId {get;set;}
}
public class AppConfig
{
    public string HotelId {get;set;}="";
    public string DeviceId {get;set;}=Guid.NewGuid().ToString();
    public string ServerUrl {get;set;}="https://ys-macmini.tail984bfd.ts.net:8443";
    public SelectedApplication SelectedApp {get;set;}
    public bool RegionsRelative {get;set;}
    public List<CaptureRegion> Regions {get;set;}=new();
    public int IntervalMs {get;set;}=1500;
    public int OcrScale {get;set;}=3;
    public bool AutoUpdate {get;set;}=true;
    public bool ShareEvidence {get;set;}=true;
    public bool RestoreMinimized {get;set;}=true;
    public string EnrollmentCode {get;set;}="";
    public string UpdatePublicKey {get;set;}="";
    public string DeviceSecret {get;set;}=""; // DPAPI CurrentUser encrypted
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RmsLink");
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RmsLink");
    public static string FilePath => Path.Combine(Dir,"config.json");
    public static AppConfig Load()
    {
        Directory.CreateDirectory(Dir);
        AppConfig cfg;
        if(File.Exists(FilePath)) cfg=JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath),JsonDefaults.Options) ?? throw new Exception("설정 파일이 비어 있습니다.");
        else cfg=new();
        var bootstrap=Path.Combine(InstallDir,"bootstrap.json");
        if(File.Exists(bootstrap)) {
            using var b=JsonDocument.Parse(File.ReadAllText(bootstrap));var r=b.RootElement;
            if(r.TryGetProperty("serverUrl",out var s)) cfg.ServerUrl=s.GetString();
            if(r.TryGetProperty("enrollmentCode",out var e)) cfg.EnrollmentCode=e.GetString();
            if(r.TryGetProperty("updatePublicKey",out var k)) cfg.UpdatePublicKey=k.GetString();
        }
        if(!Uri.TryCreate(cfg.ServerUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo!="" || uri.AbsolutePath!="/" || uri.Query!="") throw new Exception("HTTPS 서버 주소 오류");
        cfg.IntervalMs=Math.Clamp(cfg.IntervalMs,1000,30000); cfg.OcrScale=Math.Clamp(cfg.OcrScale,1,5);
        return cfg;
    }
    public string GetToken()=>string.IsNullOrEmpty(DeviceSecret)?"":Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(DeviceSecret),null,DataProtectionScope.CurrentUser));
    public void SetToken(string token){DeviceSecret=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token),null,DataProtectionScope.CurrentUser));Save();}
    public void Save(){Directory.CreateDirectory(Dir);var tmp=FilePath+".tmp";File.WriteAllText(tmp,JsonDefaults.Serialize(this));File.Move(tmp,FilePath,true);}
}
