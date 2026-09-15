using System.Text.Json;
using System.IO.Compression;
namespace RmsLink;
public static class Diagnostics
{
    public static int RuntimeTest(string output)
    {
        try {
            var now=DateTimeOffset.Now;var parsed=EventParser.Parse("101 문열림 "+now.ToString("HH:mm:ss"),now,new(),out _);
            if(parsed.Count!=1||parsed[0].Code!="DOOR_OPEN")throw new Exception("Parser failure");
            var data=System.Text.Encoding.UTF8.GetBytes("rmslink-check");
            var encrypted=System.Security.Cryptography.ProtectedData.Protect(data,null,System.Security.Cryptography.DataProtectionScope.CurrentUser);
            if(!data.SequenceEqual(System.Security.Cryptography.ProtectedData.Unprotect(encrypted,null,System.Security.Cryptography.DataProtectionScope.CurrentUser)))throw new Exception("DPAPI failure");
            File.WriteAllText(output,JsonDefaults.Serialize(new {passed=true,version=Updater.Version,windows=Environment.OSVersion.ToString(),ocrLanguages=OcrService.AvailableLanguages(),physicalKeytechVerified=false}));return 0;
        }catch(Exception ex){File.WriteAllText(output,JsonDefaults.Serialize(new {passed=false,error=ex.ToString()}));return 1;}
    }
    public static int RunSelfTest()
    {
        Directory.CreateDirectory(AppConfig.Dir);return RuntimeTest(Path.Combine(AppConfig.Dir,"selftest-report.json"));
    }
    public static string ExportDiagnostics()
    {
        string tmp=Path.Combine(Path.GetTempPath(),"rmslink-diag-"+Guid.NewGuid()),output=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"RmsLink-진단-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip");
        Directory.CreateDirectory(tmp);
        try {
            foreach(var file in Directory.GetFiles(AppConfig.Dir,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(7))File.Copy(file,Path.Combine(tmp,Path.GetFileName(file)));
            var cfg=AppConfig.Load();File.WriteAllText(Path.Combine(tmp,"summary.json"),JsonDefaults.Serialize(new {cfg.HotelId,cfg.DeviceId,cfg.Regions,cfg.ServerUrl,cfg.AutoUpdate,version=Updater.Version,ocrLanguages=OcrService.AvailableLanguages()}));
            ZipFile.CreateFromDirectory(tmp,output);return output;
        }finally{Directory.Delete(tmp,true);}
    }
}
