using System.Drawing.Imaging;
namespace RmsLink;
public sealed class LineDiag {public string Text;public ParsedEvent Event;public string Reason;public bool IsNew;}
public sealed class RegionDiag {public int Index;public Bitmap LastImage;public List<LineDiag> Lines=new();public DateTime LastOcrAt=DateTime.MinValue;}
public sealed class Worker:IDisposable
{
    readonly AppConfig cfg;readonly OcrService ocr;readonly CancellationTokenSource cts=new();Task loop;
    readonly object gate=new();readonly List<RegionDiag> diags=new();readonly Dictionary<string,DateTimeOffset> seen=new();
    AdapterProfile profile; Task<List<string>> accessibilityTask;long accessibilityHandle; DateTime lastAccessibilityStart=DateTime.MinValue;
    public readonly ControlSink Sink;public long OcrRuns,EventsFound;public DateTime LastCycleAt=DateTime.MinValue;public string OcrLang=>ocr?.LanguageTag??"없음";
    public Worker(AppConfig cfg,OcrService ocr){this.cfg=cfg;this.ocr=ocr;profile=AdapterProfile.Load(cfg.HotelId);Sink=new(cfg){ProfileRevision=profile.Revision};Sink.ProfileChanged=p=>{profile=p;};}
    public void Start()=>loop=Task.Run(Loop);
    async Task Loop()
    {
        DateTime nextHeartbeat=DateTime.MinValue;DateTime nextImage=DateTime.MinValue;string lastFingerprint="";bool previousFailure=false;
        while(!cts.IsCancellationRequested) {
            var errors=new List<string>(); var candidates=WindowProbe.Find();var lines=new List<string>();string source="none";var regions=cfg.Regions.ToList();var activeProfile=profile;
            var window=candidates.Count==1?candidates[0]:null;
            bool desktop=WindowProbe.DesktopAvailable();
            try {
                if(!desktop)errors.Add("DESKTOP_LOCKED: Windows 잠금 또는 데스크톱 접근 불가");
                if(window!=null && desktop && !window.Minimized) {
                    if(accessibilityTask==null || (accessibilityTask.IsCompleted && DateTime.UtcNow-lastAccessibilityStart>TimeSpan.FromSeconds(3))) {
                        accessibilityHandle=window.Handle;lastAccessibilityStart=DateTime.UtcNow;
                        accessibilityTask=Task.Run(()=>WindowProbe.ReadAccessibleRows(window));
                    }
                    if(accessibilityTask?.IsCompletedSuccessfully==true && accessibilityHandle==window.Handle) {
                        var accessible=accessibilityTask.Result;
                        // Only prefer UIA when actual rows parse; otherwise use OCR.
                        if(accessible.Any(l=>EventParser.Parse(l,DateTimeOffset.Now,activeProfile,out _).Count>0)) {lines=accessible;source="uia";}
                    }
                    if(accessibilityTask?.IsFaulted==true)errors.Add("UIA_UNAVAILABLE: 화면 OCR로 대체");
                    if(accessibilityTask!=null && !accessibilityTask.IsCompleted && DateTime.UtcNow-lastAccessibilityStart>TimeSpan.FromSeconds(6))errors.Add("UIA_TIMEOUT: 응답 없는 접근성 공급자, OCR로 대체");
                }
                if(window?.Minimized==true && regions.Count==0)errors.Add("WINDOW_MINIMIZED: RMS 창을 복원해 주세요");
                if(regions.Count==0 && window!=null && !window.Minimized && window.W>0 && window.H>0)regions.Add(new(){X=window.X,Y=window.Y,W=window.W,H=window.H});
                if(regions.Count==0 && source=="none") errors.Add(candidates.Count>1?"WINDOW_AMBIGUOUS: 여러 RMS 창, 로그 영역 선택 필요":"WINDOW_NOT_FOUND: RMS 실행 또는 로그 영역 선택 필요");
                Bitmap evidence=null;
                try {
                    if(source=="none" && regions.Count>0 && desktop) {
                        if(ocr==null)errors.Add("OCR_MISSING: 한국어 Windows OCR 언어팩 필요");
                        else if(!OcrLang.StartsWith("ko"))errors.Add("OCR_KOREAN_MISSING: 한국어 OCR 언어팩 필요");
                        source="ocr";
                        var newDiags=new List<RegionDiag>();
                        foreach(var r in regions.Take(4)) {
                            if(r.W<20||r.H<20||r.W>8000||r.H>8000||!SystemInformation.VirtualScreen.Contains(r.Rect)){errors.Add("REGION_INVALID: 화면 해상도 변경, 영역 재지정 필요");continue;}
                            using var bmp=CaptureService.Capture(r.Rect);
                            if(evidence==null)evidence=(Bitmap)bmp.Clone();
                            var read=ocr==null?new List<string>():await ocr.ReadLinesAsync(bmp,activeProfile.OcrScale);Interlocked.Increment(ref OcrRuns);lines.AddRange(read.Take(300));
                            newDiags.Add(new(){Index=newDiags.Count,LastImage=(Bitmap)bmp.Clone(),LastOcrAt=DateTime.Now});
                        }
                        lock(gate){foreach(var d in diags)d.LastImage?.Dispose();diags.Clear();diags.AddRange(newDiags);}
                    }
                    var found=new List<ParsedEvent>();var diagLines=new List<LineDiag>();var unmatched=new List<object>();var now=DateTimeOffset.Now;
                    foreach(var line in lines.Take(300)) {
                        var parsed=EventParser.Parse(line,now,activeProfile,out var reason);bool fresh=false;
                        foreach(var ev in parsed) {
                            // Snapshot observations renew freshness; log events never do.
                            if(ev.Kind=="snapshot" || !seen.ContainsKey(ev.DedupKey)){found.Add(ev);fresh=true;}
                        }
                        if(parsed.Count==0 && unmatched.Count<40)unmatched.Add(new{text=line[..Math.Min(line.Length,300)],reason});
                        diagLines.Add(new(){Text=line,Event=parsed.FirstOrDefault(),Reason=reason,IsNew=fresh});
                    }
                    if(lines.Count==0 && source!="none")errors.Add("NO_TEXT: 빈 화면·다른 창 가림·권한·언어팩 확인");
                    if(lines.Count>0 && !diagLines.Any(l=>l.Event!=null))errors.Add("PARSE_NO_MATCH: 객실/시각/이벤트 어휘를 판독하지 못함");
                    string fingerprint=string.Join("|",errors)+string.Join("|",lines);
                    bool changed=fingerprint!=lastFingerprint; bool due=DateTime.UtcNow>=nextHeartbeat;
                    if(changed||due||found.Any(e=>e.Kind=="event")) {
                        string image=null;
                        if(cfg.ShareEvidence && evidence!=null && DateTime.UtcNow>=nextImage) {
                            using var thumb=new Bitmap(evidence,new Size(Math.Min(1280,evidence.Width),Math.Max(1,(int)(evidence.Height*Math.Min(1,1280d/evidence.Width)))));
                            using var ms=new MemoryStream();thumb.Save(ms,ImageFormat.Jpeg);if(ms.Length<400000){image=Convert.ToBase64String(ms.ToArray());nextImage=DateTime.UtcNow.AddSeconds(30);}
                        }
                        var observation=new {capturedAt=DateTimeOffset.UtcNow,source,ocrLanguage=OcrLang,os=Environment.OSVersion.ToString(),desktopAvailable=desktop,remoteSession=SystemInformation.TerminalServerSession,windows=candidates,regions,errors,lines=diagLines.Take(100).Select(l=>new {text=l.Text,code=l.Event?.Code,room=l.Event?.Room,reason=l.Reason}),unmatched,image};
                        Sink.Enqueue(observation,found);
                        foreach(var ev in found.Where(e=>e.Kind=="event"))seen[ev.DedupKey]=ev.ObservedAt;
                        if(seen.Count>20000)foreach(var key in seen.OrderBy(x=>x.Value).Take(5000).Select(x=>x.Key).ToArray())seen.Remove(key);
                        EventsFound+=found.Count;lastFingerprint=fingerprint;nextHeartbeat=DateTime.UtcNow.AddSeconds(15);
                    }
                    lock(gate){if(diags.Count==0)diags.Add(new(){Index=0});diags[0].Lines=diagLines;diags[0].LastOcrAt=DateTime.Now;}
                }finally{evidence?.Dispose();}
                previousFailure=false;
            }catch(Exception ex) {
                Logger.Error("수집 실패: "+ex.Message);
                if(!previousFailure || DateTime.UtcNow>=nextHeartbeat)try{Sink.Enqueue(new {capturedAt=DateTimeOffset.UtcNow,source,ocrLanguage=OcrLang,errors=new[]{"CAPTURE_FAILURE: "+ex.Message},lines=Array.Empty<object>(),windows=candidates},Array.Empty<ParsedEvent>());nextHeartbeat=DateTime.UtcNow.AddSeconds(15);}catch(Exception queueError){Sink.LastError=queueError.Message;}
                previousFailure=true;
            }
            LastCycleAt=DateTime.Now;
            try{await Task.Delay(activeProfile.PollMs,cts.Token);}catch(OperationCanceledException){break;}
        }
    }
    public List<(Bitmap Image,List<LineDiag> Lines,DateTime At)> SnapshotDiagnostics(){lock(gate)return diags.Select(d=>(d.LastImage==null?null:(Bitmap)d.LastImage.Clone(),new List<LineDiag>(d.Lines),d.LastOcrAt)).ToList();}
    public void Dispose(){cts.Cancel();try{loop?.Wait(5000);}catch{}Sink.Dispose();lock(gate)foreach(var d in diags)d.LastImage?.Dispose();}
}
