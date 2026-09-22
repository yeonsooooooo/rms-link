using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace RmsLink.Shared;

public static class SupportBundle
{
    public static string Export(string install, string app, string destination)
    {
        string temp=Path.Combine(Path.GetTempPath(),"rmslink-diag-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);Directory.CreateDirectory(destination);
        var issues=new List<string>();
        string output=Path.Combine(destination,"RmsLink-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]+".zip");
        try {
            void Copy(string source,string name) {
                if(!File.Exists(source))return;
                try { var text=File.ReadAllText(source);if(text.Length>500000)text=text[^500000..];
                    text=Regex.Replace(text,@"\b[a-fA-F0-9]{48,64}\b","[credential-or-digest-redacted]");
                    File.WriteAllText(Path.Combine(temp,name),text);
                }catch(Exception ex){issues.Add(name+": "+ex.GetType().Name);}
            }
            foreach(var name in new[]{"installer.log","installation-status.json","launcher-status.json","install-runtime.json","failed-update.json","current.txt"})Copy(Path.Combine(install,name),"install-"+name);
            foreach(var name in new[]{"connection-status.json","capture-status.json","selftest-report.json"})Copy(Path.Combine(app,name),name);
            foreach(var dir in new[]{app,Path.GetTempPath()}) {
                try {if(Directory.Exists(dir))foreach(var f in Directory.GetFiles(dir,dir==app?"*.log":"RmsLink-crash-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(7))Copy(f,(dir==app?"app-":"temp-")+Path.GetFileName(f));}catch(Exception ex){issues.Add(ex.GetType().Name);}
            }
            foreach(var name in new[]{"installation-status.json","launcher-status.json"})Copy(Path.Combine(Path.GetTempPath(),"RmsLink-"+name),"fallback-"+name);
            // A malformed config must not prevent collecting installation failures.
            try {
                using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(app,"config.json")));
                var summary=new Dictionary<string,JsonElement>();
                foreach(var name in new[]{"hotelId","deviceId","serverUrl","autoUpdate","shareEvidence","restoreMinimized","selectedApp","regions"})
                    if(config.RootElement.TryGetProperty(name,out var value))summary[name]=value.Clone();
                File.WriteAllText(Path.Combine(temp,"settings-summary.json"),JsonSerializer.Serialize(summary));
            } catch(Exception ex) {issues.Add("config: "+ex.GetType().Name);}
            File.WriteAllText(Path.Combine(temp,"environment.json"),JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,os=Environment.OSVersion.ToString(),architecture=System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),is64Bit=Environment.Is64BitOperatingSystem,installed=File.Exists(Path.Combine(install,"current.txt")),issues}));
            File.WriteAllText(Path.Combine(temp,"READ-ME.txt"),"Installation, connection and capture are separate checks. See installation-status, connection-status and capture-status. Missing installation-status means the installer may not have reached Main. Config secrets, enrollment codes, queues and screenshots are excluded. Physical door/key verification is still required.");
            ZipFile.CreateFromDirectory(temp,output);return output;
        } finally {try{Directory.Delete(temp,true);}catch{}}
    }
}
