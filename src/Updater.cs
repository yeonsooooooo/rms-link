using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
namespace RmsLink;
public sealed class SignedEnvelope { public string Payload {get;set;} public string Signature {get;set;} }
public sealed class UpdateOffer {public AdapterProfile Profile{get;set;} public ReleaseOffer Release{get;set;} public DateTimeOffset ExpiresAt{get;set;} }
public sealed class ReleaseOffer {public string Version{get;set;}public string Sha256{get;set;}public long Size{get;set;}public string Path{get;set;} }
public static class Updater
{
    public static string Version=>typeof(Updater).Assembly.GetName().Version?.ToString(3)??"0.2.0";
    public static UpdateOffer Verify(SignedEnvelope envelope,string key)
    {
        if(envelope==null||string.IsNullOrWhiteSpace(key))throw new Exception("설치 파일에 업데이트 검증 키가 없습니다.");
        using var rsa=RSA.Create();rsa.ImportFromPem(key);
        var payload=Convert.FromBase64String(envelope.Payload);
        if(!rsa.VerifyData(payload,Convert.FromBase64String(envelope.Signature),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))throw new Exception("업데이트 서명 검증 실패");
        var offer=JsonSerializer.Deserialize<UpdateOffer>(payload,JsonDefaults.Options);
        if(offer.ExpiresAt<DateTimeOffset.UtcNow||offer.ExpiresAt>DateTimeOffset.UtcNow.AddDays(2))throw new Exception("업데이트 안내 만료 또는 Windows 시계 오류");
        return offer;
    }
    public static async Task Stage(HttpClient http,AppConfig cfg,ReleaseOffer release,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(release.Version,@"^\d+\.\d+\.\d+$")||!System.Text.RegularExpressions.Regex.IsMatch(release.Sha256,@"^[a-f0-9]{64}$")||release.Path!="agent/packages/"+release.Sha256||release.Size<1000||release.Size>300_000_000)throw new Exception("업데이트 패키지 정보 오류");
        string root=AppConfig.InstallDir,versionDir=Path.Combine(root,"versions",release.Version),stage=versionDir+".staging",zip=Path.Combine(root,"update.zip");
        Directory.CreateDirectory(root);
        using(var response=await http.GetAsync(release.Path,HttpCompletionOption.ResponseHeadersRead,ct)) {
            response.EnsureSuccessStatusCode();using var input=await response.Content.ReadAsStreamAsync(ct);using var output=File.Create(zip);
            byte[] buffer=new byte[65536];long total=0;int n;while((n=await input.ReadAsync(buffer,ct))>0){total+=n;if(total>release.Size)throw new Exception("패키지 크기 초과");await output.WriteAsync(buffer.AsMemory(0,n),ct);}
            if(total!=release.Size)throw new Exception("다운로드 크기 불일치");
        }
        using(var f=File.OpenRead(zip))if(Convert.ToHexString(await SHA256.HashDataAsync(f,ct)).ToLowerInvariant()!=release.Sha256)throw new Exception("패키지 해시 불일치");
        if(Directory.Exists(stage))Directory.Delete(stage,true);Directory.CreateDirectory(stage);
        using(var archive=ZipFile.OpenRead(zip)) {
            long expanded=0;
            foreach(var entry in archive.Entries) {
                string target=Path.GetFullPath(Path.Combine(stage,entry.FullName.Replace('\\','/')));
                if(!target.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||entry.FullName.Contains(':'))throw new Exception("압축 경로 오류");
                expanded+=entry.Length;if(expanded>800_000_000)throw new Exception("압축 해제 크기 초과");
                if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(target);continue;}
                Directory.CreateDirectory(Path.GetDirectoryName(target));entry.ExtractToFile(target,true);
            }
        }
        string exe=Path.Combine(stage,"RmsLink.exe");
        if(!File.Exists(exe))throw new Exception("실행 파일 없음");
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--runtime-test");start.ArgumentList.Add(Path.Combine(stage,"runtime-test.json"));
        using(var p=Process.Start(start)) {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try{await p.WaitForExitAsync(timeout.Token);}catch{try{p.Kill(true);}catch{}throw;}
            if(p.ExitCode!=0)throw new Exception("새 버전 자체 점검 실패");
        }
        if(Directory.Exists(versionDir))Directory.Delete(versionDir,true);Directory.Move(stage,versionDir);
        // Separate launcher owns the version pointer and rollback watchdog.
        File.WriteAllText(Path.Combine(root,"pending.json.tmp"),JsonDefaults.Serialize(new {version=release.Version,previous=Version}));
        File.Move(Path.Combine(root,"pending.json.tmp"),Path.Combine(root,"pending.json"),true);
        var launcher=Path.Combine(root,"RmsLinkLauncher.exe");
        if(!File.Exists(launcher))throw new Exception("설치 관리자가 없습니다. 설치 파일로 먼저 설치해 주세요.");
        var psi=new ProcessStartInfo(launcher){UseShellExecute=false};psi.ArgumentList.Add("--update");psi.ArgumentList.Add(Environment.ProcessId.ToString());Process.Start(psi);
    }
}
