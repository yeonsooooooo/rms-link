using System.Net.Http.Json;
using RmsLink.Shared;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace RmsLink;
public sealed class ControlSink:IDisposable
{
    public readonly StageReport ConnectionReport;
    readonly string dataDirectory;
    readonly SemaphoreSlim connectionGate=new(1,1);
    bool enrolled;
    string networkStage="SERVER";
    readonly AppConfig cfg; readonly CancellationTokenSource cts=new(); Task loop,updateTask;
    readonly string queueDir; readonly string sessionId=Guid.NewGuid().ToString();
    readonly HttpClient http;
    public long SentCount; public string LastError=""; public DateTime LastSentAt=DateTime.MinValue;
    public string UpdateStatus="업데이트 대기"; public bool ManualUpdateRequested;
    public Action ExitForUpdate; public Action<AdapterProfile> ProfileChanged;
    public int ProfileRevision=1;
    public int PendingCount=>Directory.GetFiles(queueDir,"*.json").Length;
    public ControlSink(AppConfig config, HttpMessageHandler handler=null, string storageDirectory=null)
    {
        dataDirectory=storageDirectory??AppConfig.Dir;
        ConnectionReport=new(Path.Combine(dataDirectory,"connection-status.json"));
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(25)};
        cfg=config;UpdateStatus=cfg.AutoUpdate?"자동 업데이트 확인 대기":"자동 업데이트 꺼짐";queueDir=Path.Combine(dataDirectory,"outbox");Directory.CreateDirectory(queueDir);
        if(string.IsNullOrEmpty(cfg.DeviceSecret))cfg.SetToken(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        http.BaseAddress=new Uri(cfg.ServerUrl.TrimEnd('/')+"/");http.DefaultRequestHeaders.Authorization=new("Bearer",cfg.GetToken());
    }
    public void Start()=>loop=Task.Run(Loop);
    public void Enqueue(object observation,IEnumerable<ParsedEvent> events,int profileRevision)
    {
        if(PendingCount>=15000)throw new IOException("오프라인 보관 한도 도달: 전송 연결과 저장 공간 확인 필요. 이벤트를 버리지 않기 위해 수집을 대기합니다.");
        var batch=new {id=Guid.NewGuid().ToString(),deviceId=cfg.DeviceId,sessionId,hotelId=cfg.HotelId,version=Updater.Version,profileRevision,sentAt=DateTimeOffset.UtcNow,observation,events=events.Select(e=>new {e.Room,e.Code,e.Kind,e.RawLine,e.OccurredAt,e.ObservedAt,e.Source}).ToArray()};
        var name=Path.Combine(queueDir,DateTime.UtcNow.Ticks+"-"+batch.id+".json");
        var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonDefaults.Serialize(batch)),null,DataProtectionScope.CurrentUser);
        File.WriteAllBytes(name+".tmp",bytes);File.Move(name+".tmp",name);
    }
    public async Task<string> CheckConnection()
    {
        await connectionGate.WaitAsync(cts.Token);
        try {
            networkStage="SERVER"; ConnectionReport.Set(networkStage,"running","HTTPS 서버 확인 중");
            await AgentConnection.Health(http,cts.Token); ConnectionReport.Set(networkStage,"passed","RmsLink HTTPS 응답 확인");
            await Enroll(); await Heartbeat();
            return "서버 응답 · 기기 등록 · 호텔 연결 확인 완료. 화면 판독과 실제 문·키 대조를 계속하세요.";
        } catch(Exception ex) {
            var message=AgentConnection.Describe(ex,networkStage);ConnectionReport.Set(networkStage,"failed",message);
            throw new IOException(message,ex);
        } finally { connectionGate.Release(); }
    }
    async Task Enroll() {
        networkStage="ENROLL";ConnectionReport.Set(networkStage,"running","기기 등록 확인 중");
        await AgentConnection.Enroll(http,cfg.DeviceId,cfg.EnrollmentCode,Environment.MachineName,cts.Token);
        enrolled=true;ConnectionReport.Set(networkStage,"passed","기기 등록 확인");
    }
    async Task Heartbeat() {
        networkStage="HEARTBEAT";
        var rejected=Path.Combine(dataDirectory,"rejected");
        int rejectedCount=Directory.Exists(rejected)?Directory.GetFiles(rejected,"*.json").Length:0;
        using var r=await http.PostAsJsonAsync("agent/heartbeat",new {deviceId=cfg.DeviceId,hotelId=cfg.HotelId,sessionId,version=Updater.Version,profileRevision=ProfileRevision,pending=PendingCount,updateStatus=UpdateStatus+(rejectedCount>0?" · 거부된 전송 "+rejectedCount+"건 보존":"")},JsonDefaults.Options,cts.Token);
        await AgentConnection.Ensure(r,networkStage,cts.Token);await AgentConnection.RequireOk(r,networkStage,cts.Token);
        ConnectionReport.Set(networkStage,"passed","호텔 연결 보고 수신 확인");
    }
    async Task Loop()
    {
        DateTime updateDue=DateTime.MinValue;int failures=0;
        while(!cts.IsCancellationRequested)try {
            string rejected=Path.Combine(dataDirectory,"rejected");Directory.CreateDirectory(rejected);
            await connectionGate.WaitAsync(cts.Token);
            try { if(!enrolled) await Enroll(); await Heartbeat(); }
            finally { connectionGate.Release(); }
            foreach(var file in Directory.GetFiles(queueDir,"*.json").OrderBy(x=>x).Take(40)) {
                var data=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser);
                using var content=new ByteArrayContent(data);content.Headers.ContentType=new("application/json");
                networkStage="OBSERVATIONS";
                using var r=await http.PostAsync("agent/observations",content,cts.Token);
                if((int)r.StatusCode==400){File.Move(file,Path.Combine(rejected,Path.GetFileName(file)),true);LastError="OBSERVATIONS_HTTP_400: 서버가 거부한 자료를 보존했습니다. 앱·서버 버전과 현장 진단을 확인하세요.";ConnectionReport.Set("OBSERVATIONS","failed",LastError);Logger.Error(LastError);continue;}
                await AgentConnection.Ensure(r,networkStage,cts.Token);
                using var ack=JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
                using var original=JsonDocument.Parse(data);
                if(ack.RootElement.GetProperty("ack").GetString()!=original.RootElement.GetProperty("id").GetString())throw new Exception("전송 확인 ID 불일치");
                File.Delete(file);Interlocked.Increment(ref SentCount);LastSentAt=DateTime.Now;ConnectionReport.Set("OBSERVATIONS","passed","화면 자료 수신 확인");
            }
            if((DateTime.UtcNow>=updateDue || ManualUpdateRequested) && (updateTask==null || updateTask.IsCompleted)) {
                bool manual=ManualUpdateRequested;ManualUpdateRequested=false;
                // Large downloads and runtime checks must not stop live heartbeats/outbox replay.
                if(cfg.AutoUpdate || manual) updateTask=Task.Run(CheckUpdates);
                updateDue=DateTime.UtcNow.AddMinutes(2);
            }
            LastError=Directory.GetFiles(rejected,"*.json").Length>0?"OBSERVATIONS_REJECTED: 서버가 거부한 전송 자료가 보존되어 있습니다. 진단 파일을 확인하세요.":"";failures=0;await Task.Delay(5000,cts.Token);
        }catch(OperationCanceledException) when(cts.IsCancellationRequested){break;}
        catch(Exception ex){LastError=AgentConnection.Describe(ex,networkStage);ConnectionReport.Set(networkStage,"failed",LastError);Logger.Error(LastError);failures++;try{await Task.Delay(Math.Min(60000,2000*(1<<Math.Min(failures,5)))+Random.Shared.Next(1000),cts.Token);}catch{break;}}
    }
    async Task CheckUpdates()
    {
        try {
            using var r=await http.GetAsync("agent/updates?hotelId="+Uri.EscapeDataString(cfg.HotelId),cts.Token);r.EnsureSuccessStatusCode();
            var envelope=await r.Content.ReadFromJsonAsync<SignedEnvelope>(JsonDefaults.Options,cts.Token);
            var offer=Updater.Verify(envelope,cfg.UpdatePublicKey);
            if(offer.Profile!=null && offer.Profile.Revision>ProfileRevision) {
                offer.Profile.Validate(cfg.HotelId);
                string path=Path.Combine(dataDirectory,"profile-"+cfg.HotelId+".json");
                File.WriteAllText(path+".tmp",JsonDefaults.Serialize(offer.Profile));File.Move(path+".tmp",path,true);
                ProfileChanged?.Invoke(offer.Profile);ProfileRevision=offer.Profile.Revision;
            }
            UpdateStatus=cfg.AutoUpdate?"최신 · 자동 업데이트 켜짐 · 프로필 r"+ProfileRevision:"수동 확인 완료 · 자동 업데이트 꺼짐";
            if(offer.Release!=null && System.Version.Parse(offer.Release.Version)>System.Version.Parse(Updater.Version)) {
                var failed=Path.Combine(AppConfig.InstallDir,"failed-update.json");
                if(File.Exists(failed)) {
                    using var failure=JsonDocument.Parse(File.ReadAllText(failed));
                    if(failure.RootElement.GetProperty("version").GetString()==offer.Release.Version){UpdateStatus="이전 버전 복구됨 · 실패 버전 재설치 보류";return;}
                }
                UpdateStatus="앱 "+offer.Release.Version+" 다운로드 및 검증";
                await Updater.Stage(http,cfg,offer.Release,cts.Token);
                UpdateStatus="재시작하여 업데이트 적용";ExitForUpdate?.Invoke();
            }
        }catch(OperationCanceledException) when(cts.IsCancellationRequested) {}
        catch(Exception ex){UpdateStatus="업데이트 실패: "+ex.Message;Logger.Error(UpdateStatus);}
    }
    public void Dispose(){cts.Cancel();try{Task.WhenAll(new[]{loop,updateTask}.Where(t=>t!=null)).Wait(3000);}catch{}http.Dispose();}
}
