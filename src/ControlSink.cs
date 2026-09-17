using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace RmsLink;
public sealed class ControlSink:IDisposable
{
    readonly AppConfig cfg; readonly CancellationTokenSource cts=new(); readonly Task loop;
    readonly string queueDir; readonly string sessionId=Guid.NewGuid().ToString();
    readonly HttpClient http=new(new HttpClientHandler { AllowAutoRedirect=false }){Timeout=TimeSpan.FromSeconds(25)};
    public long SentCount; public string LastError=""; public DateTime LastSentAt=DateTime.MinValue;
    public string UpdateStatus="업데이트 대기"; public bool ManualUpdateRequested;
    public Action ExitForUpdate; public Action<AdapterProfile> ProfileChanged;
    public int ProfileRevision=1;
    public int PendingCount=>Directory.GetFiles(queueDir,"*.json").Length;
    public ControlSink(AppConfig config)
    {
        cfg=config;queueDir=Path.Combine(AppConfig.Dir,"outbox");Directory.CreateDirectory(queueDir);
        if(string.IsNullOrEmpty(cfg.DeviceSecret))cfg.SetToken(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        http.BaseAddress=new Uri(cfg.ServerUrl.TrimEnd('/')+"/");http.DefaultRequestHeaders.Authorization=new("Bearer",cfg.GetToken());
        loop=Task.Run(Loop);
    }
    public void Enqueue(object observation,IEnumerable<ParsedEvent> events,int profileRevision)
    {
        if(PendingCount>=15000)throw new IOException("오프라인 보관 한도 도달: 전송 연결과 저장 공간 확인 필요. 이벤트를 버리지 않기 위해 수집을 대기합니다.");
        var batch=new {id=Guid.NewGuid().ToString(),deviceId=cfg.DeviceId,sessionId,hotelId=cfg.HotelId,version=Updater.Version,profileRevision,sentAt=DateTimeOffset.UtcNow,observation,events=events.Select(e=>new {e.Room,e.Code,e.Kind,e.RawLine,e.OccurredAt,e.ObservedAt}).ToArray()};
        var name=Path.Combine(queueDir,DateTime.UtcNow.Ticks+"-"+batch.id+".json");
        var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonDefaults.Serialize(batch)),null,DataProtectionScope.CurrentUser);
        File.WriteAllBytes(name+".tmp",bytes);File.Move(name+".tmp",name);
    }
    public async Task<string> CheckConnection(){using var r=await http.GetAsync("health",cts.Token);r.EnsureSuccessStatusCode();return "맥 미니 HTTPS 연결 정상";}
    async Task Loop()
    {
        bool enrolled=false; DateTime updateDue=DateTime.MinValue;int failures=0;
        while(!cts.IsCancellationRequested)try {
            string rejected=Path.Combine(AppConfig.Dir,"rejected");Directory.CreateDirectory(rejected);
            if(!enrolled){using var r=await http.PostAsJsonAsync("agent/enroll",new {deviceId=cfg.DeviceId,enrollmentCode=cfg.EnrollmentCode,machine=Environment.MachineName},JsonDefaults.Options,cts.Token);r.EnsureSuccessStatusCode();enrolled=true;}
            // Keep live heartbeat flowing even when old event batches need replay.
            using(var r=await http.PostAsJsonAsync("agent/heartbeat",new {deviceId=cfg.DeviceId,hotelId=cfg.HotelId,sessionId,version=Updater.Version,profileRevision=ProfileRevision,pending=PendingCount,updateStatus=UpdateStatus+(Directory.GetFiles(rejected,"*.json").Length>0?" · 거부된 전송 "+Directory.GetFiles(rejected,"*.json").Length+"건 보존":"")},JsonDefaults.Options,cts.Token)) r.EnsureSuccessStatusCode();
            foreach(var file in Directory.GetFiles(queueDir,"*.json").OrderBy(x=>x).Take(40)) {
                var data=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser);
                using var content=new ByteArrayContent(data);content.Headers.ContentType=new("application/json");
                using var r=await http.PostAsync("agent/observations",content,cts.Token);
                if((int)r.StatusCode==400){File.Move(file,Path.Combine(rejected,Path.GetFileName(file)),true);Logger.Error("서버가 거부한 전송 자료를 rejected에 보존: "+await r.Content.ReadAsStringAsync(cts.Token));continue;}
                r.EnsureSuccessStatusCode();
                using var ack=JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
                using var original=JsonDocument.Parse(data);
                if(ack.RootElement.GetProperty("ack").GetString()!=original.RootElement.GetProperty("id").GetString())throw new Exception("전송 확인 ID 불일치");
                File.Delete(file);Interlocked.Increment(ref SentCount);LastSentAt=DateTime.Now;
            }
            if(DateTime.UtcNow>=updateDue || ManualUpdateRequested) {
                bool manual=ManualUpdateRequested;ManualUpdateRequested=false;
                if(cfg.AutoUpdate || manual) {
                    try {
                        using var r=await http.GetAsync("agent/updates?hotelId="+Uri.EscapeDataString(cfg.HotelId),cts.Token);r.EnsureSuccessStatusCode();
                        var envelope=await r.Content.ReadFromJsonAsync<SignedEnvelope>(JsonDefaults.Options,cts.Token);
                        var offer=Updater.Verify(envelope,cfg.UpdatePublicKey);
                        if(offer.Profile!=null && offer.Profile.Revision>ProfileRevision) {
                            offer.Profile.Validate(cfg.HotelId);
                            string path=Path.Combine(AppConfig.Dir,"profile-"+cfg.HotelId+".json");
                            File.WriteAllText(path+".tmp",JsonDefaults.Serialize(offer.Profile));File.Move(path+".tmp",path,true);
                            ProfileChanged?.Invoke(offer.Profile);ProfileRevision=offer.Profile.Revision;
                        }
                        UpdateStatus="최신 · 프로필 r"+ProfileRevision;
                        if(offer.Release!=null && System.Version.Parse(offer.Release.Version)>System.Version.Parse(Updater.Version)) {
                            var failed=Path.Combine(AppConfig.InstallDir,"failed-update.json");
                            if(File.Exists(failed) && JsonDocument.Parse(File.ReadAllText(failed)).RootElement.GetProperty("version").GetString()==offer.Release.Version){UpdateStatus="이전 버전 복구됨 · 실패 버전 재설치 보류";updateDue=DateTime.UtcNow.AddMinutes(2);continue;}
                            UpdateStatus="앱 "+offer.Release.Version+" 다운로드 및 검증";
                            await Updater.Stage(http,cfg,offer.Release,cts.Token);
                            UpdateStatus="재시작하여 업데이트 적용";ExitForUpdate?.Invoke();return;
                        }
                    }catch(Exception ex){UpdateStatus="업데이트 실패: "+ex.Message;Logger.Error(UpdateStatus);}
                }
                updateDue=DateTime.UtcNow.AddMinutes(2);
            }
            LastError="";failures=0;await Task.Delay(5000,cts.Token);
        }catch(OperationCanceledException){break;}
        catch(Exception ex){LastError="맥 미니 전송 실패: "+ex.Message;Logger.Error(LastError);failures++;try{await Task.Delay(Math.Min(60000,2000*(1<<Math.Min(failures,5)))+Random.Shared.Next(1000),cts.Token);}catch{break;}}
    }
    public void Dispose(){cts.Cancel();try{loop.Wait(3000);}catch{}http.Dispose();}
}
