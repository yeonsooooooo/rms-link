using System.Drawing.Imaging;
using RmsLink.Shared;
namespace RmsLink;
public sealed class LineDiag { public string Text; public ParsedEvent Event; public string Reason; public bool IsNew; public string Source; }
public sealed class RegionDiag { public int Index; public Bitmap LastImage; public List<LineDiag> Lines=new(); public DateTime LastOcrAt=DateTime.MinValue; }
public sealed class Worker : IDisposable
{
    readonly AppConfig cfg;
    readonly OcrService ocr;
    readonly CancellationTokenSource cts=new();
    readonly object gate=new();
    readonly List<RegionDiag> diags=new();
    readonly Dictionary<string,DateTimeOffset> seen=new();
    readonly WindowCapture capture=new();
    readonly ReadingSession reader=new();
    Task loop;
    AdapterProfile profile;
    Task<List<string>> accessibilityTask;
    long accessibilityHandle;
    DateTime lastAccessibilityStart, lastRestore=DateTime.MinValue;
    public readonly ControlSink Sink;
    readonly StageReport captureReport=new(Path.Combine(AppConfig.Dir,"capture-status.json"));
    public string LastCaptureStatus="첫 화면을 기다리는 중";
    public long OcrRuns,EventsFound;
    public DateTime LastCycleAt=DateTime.MinValue;
    public string OcrLang=>ocr?.LanguageTag??"없음";
    public Worker(AppConfig cfg,OcrService ocr)
    {
        this.cfg=cfg; this.ocr=ocr; profile=AdapterProfile.Load(cfg.HotelId);
        Sink=new(cfg){ProfileRevision=profile.Revision}; Sink.ProfileChanged=p=>profile=p;
    }
    public void Start(){Sink.Start();loop=Task.Run(Loop);}

    async Task<(List<string> Lines,string Status)> Accessible(WindowCandidate window,List<string> warnings)
    {
        if(accessibilityTask==null) {
            accessibilityHandle=window.Handle; lastAccessibilityStart=DateTime.UtcNow;
            accessibilityTask=Task.Run(()=>WindowProbe.ReadAccessibleRows(window));
        }
        if(!accessibilityTask.IsCompleted) await Task.WhenAny(accessibilityTask,Task.Delay(650,cts.Token));
        if(!accessibilityTask.IsCompleted) { warnings.Add("UIA_TIMEOUT: 접근성 응답 대기 · OCR 사용"); return (new(),"timeout"); }
        var task=accessibilityTask; accessibilityTask=null;
        // A result is consumed once; an old/hung provider must never confirm another frame.
        try {
            var lines=await task;
            if(accessibilityHandle!=window.Handle || DateTime.UtcNow-lastAccessibilityStart>=TimeSpan.FromSeconds(3)) { warnings.Add("UIA_STALE: 직접 읽기 응답이 늦어 이번 화면에는 사용하지 않았습니다"); return (new(),"stale"); }
            return (lines,lines.Count>0?"ok":"empty");
        } catch { warnings.Add("UIA_UNAVAILABLE: 접근성 조회 실패 · OCR 사용"); return (new(),"error"); }
    }

    async Task Loop()
    {
        DateTime nextHeartbeat=DateTime.MinValue,nextImage=DateTime.MinValue;
        string lastFingerprint="";
        while(!cts.IsCancellationRequested) {
            var errors=new List<string>(); var warnings=new List<string>();
            var candidates=new List<WindowCandidate>(); var result=new ReadingResult();
            var activeProfile=profile;
            var regions=cfg.RegionsRelative?cfg.Regions.ToList():new List<CaptureRegion>();
            string source="none",captureMethod="none",uiaStatus="not_attempted",ocrStatus="not_attempted";
            var uia=new List<string>(); var ocrLines=new List<string>();
            Bitmap evidence=null; bool desktop=false;
            ApplicationDetails application=null;
            try {
                desktop=WindowProbe.DesktopAvailable();
                if(!desktop) errors.Add("DESKTOP_LOCKED: Windows에 로그인하고 키텍 창을 표시하세요");
                if(cfg.SelectedApp==null) errors.Add("APP_NOT_SELECTED: 키텍 앱을 선택하세요");
                else candidates=WindowProbe.Find(cfg.SelectedApp);
                var window=candidates.Count==1?candidates[0]:null;
                if(cfg.SelectedApp!=null && window==null) errors.Add(candidates.Count>1?"WINDOW_AMBIGUOUS: 키텍 창을 다시 선택하세요":"WINDOW_NOT_FOUND: 선택한 키텍 앱을 실행하세요");
                if(desktop && window?.Minimized==true && cfg.RestoreMinimized && DateTime.UtcNow-lastRestore>TimeSpan.FromSeconds(10)) {
                    lastRestore=DateTime.UtcNow; WindowProbe.Restore(window);
                    await Task.Delay(500,cts.Token);
                    candidates=WindowProbe.Find(cfg.SelectedApp); window=candidates.Count==1?candidates[0]:null;
                    if(window==null) errors.Add("WINDOW_NOT_FOUND: 복원한 키텍 창을 다시 선택하세요");
                    if(window!=null && !window.Minimized) warnings.Add("WINDOW_RESTORED: 최소화된 키텍 창을 복원했습니다");
                }
                if(window?.Minimized==true) errors.Add("WINDOW_MINIMIZED: 키텍 창 복원이 필요합니다");
                if(desktop && window!=null && !window.Minimized) {
                    application=WindowProbe.Describe(window,cfg.SelectedApp.Vendor);
                    if(regions.Count==0) (uia,uiaStatus)=await Accessible(window,warnings);
                    else uiaStatus="skipped_region";
                    try {
                        var captured=await capture.Read(window);
                        using var whole=captured.Image; captureMethod=captured.Method;
                        if(regions.Count==0) evidence=(Bitmap)whole.Clone();
                        else {
                            var region=regions[0].Rect;
                            if(!new Rectangle(0,0,whole.Width,whole.Height).Contains(region) || region.Width<20 || region.Height<20)
                                throw new Exception("REGION_INVALID: 키텍 창 크기가 바뀌었습니다. 로그 영역을 다시 지정하세요");
                            evidence=whole.Clone(region,PixelFormat.Format32bppArgb);
                        }

                        try {
                            if(ocr!=null) { ocrLines=await ocr.ReadLinesAsync(evidence,activeProfile.OcrScale); Interlocked.Increment(ref OcrRuns); ocrStatus=ocrLines.Count>0?"ok":"empty"; }
                            else { ocrStatus="unavailable"; warnings.Add("OCR_MISSING: 한국어 Windows OCR 언어팩이 필요합니다"); }
                            if(ocr!=null && !OcrLang.StartsWith("ko")) warnings.Add("OCR_KOREAN_MISSING: 한국어 OCR을 설치하면 교차 확인할 수 있습니다");
                        } catch(Exception ex) { ocrStatus="error"; warnings.Add("OCR_FAILURE: "+ex.Message); }
                        source=uia.Count>0 ? (ocrLines.Count>0?"hybrid":"uia") : (ocrLines.Count>0?"ocr":"none");
                        result=reader.Read(uia,ocrLines,activeProfile,DateTimeOffset.Now,application.Identity+"|"+JsonDefaults.Serialize(regions));
                        errors.AddRange(result.Errors); warnings.AddRange(result.Warnings);
                        if(uia.Count+ocrLines.Count==0) errors.Add("NO_TEXT: 읽을 수 있는 텍스트가 없습니다. 언어팩·로그 영역·아이콘 화면 여부를 확인하세요");
                        else if(result.Lines.All(l=>l.Events.Count==0)) errors.Add("PARSE_NO_MATCH: 객실·시각·상태 어휘 또는 화면 설정 확인 필요");
                    } catch(Exception ex) { errors.Add(ex.Message.Contains(':')?ex.Message:"CAPTURE_FAILURE: "+ex.Message); reader.BreakContinuity(); }
                } else reader.BreakContinuity();

                // Failed capture/contradictory reading is diagnostic evidence, never accepted state.
                var accepted=errors.Count==0 ? result.Events : new List<ParsedEvent>();
                var found=accepted.Where(e=>e.Kind=="snapshot" || !seen.ContainsKey(e.DedupKey)).Take(600).ToList();
                var diagLines=new List<LineDiag>();
                foreach(var line in result.Lines)
                foreach(var parsed in line.Events.DefaultIfEmpty()) {
                    var adopted=parsed==null?null:accepted.FirstOrDefault(e=>e.Room==parsed.Room&&e.Code==parsed.Code&&(e.Kind=="snapshot"||e.OccurredAt==parsed.OccurredAt));
                    diagLines.Add(new(){Text=line.Text,Source=line.Source,Event=adopted,Reason=parsed!=null&&adopted==null?"확인 대기: 연속 판독·상충 상태·갱신 시각 확인 필요":line.Reason,IsNew=adopted!=null&&found.Contains(adopted)});
                }
                var uncertain=result.UncertainFields.Distinct().Take(1000).ToList();
                if(activeProfile.Mode=="snapshot") {
                    var visible=accepted.Select(e=>e.Room+"|"+(e.Code.StartsWith("DOOR")?"door":"key")).ToHashSet();
                    foreach(var room in activeProfile.ExpectedRooms)
                    foreach(var field in new[]{"door","key"}) if(!visible.Contains(room+"|"+field)) uncertain.Add(new(room,field));
                }
                errors=errors.Distinct().Take(30).ToList(); warnings=warnings.Distinct().Take(30).ToList();
                LastCaptureStatus=errors.Count>0?string.Join(" · ",errors):accepted.Count>0?"화면 판독 확인 · 실제 문·키 대조 필요":"화면 수집 중 · 판독 연속 확인 또는 새 이벤트 대기";
                captureReport.Set("CAPTURE",evidence==null?"failed":"passed",evidence==null?LastCaptureStatus:"선택한 창 이미지 캡처 확인");
                captureReport.Set("READING",errors.Count>0?"failed":accepted.Count>0?"passed":"waiting",LastCaptureStatus+(warnings.Count>0?" · "+string.Join(" · ",warnings):""));
                var methods=new {
                    uia=new {status=uiaStatus,lineCount=uia.Count,candidateCount=result.Lines.Where(l=>l.Source=="uia").Sum(l=>l.Events.Count),acceptedCount=accepted.Count(e=>e.Source is "uia" or "hybrid")},
                    ocr=new {status=ocrStatus,lineCount=ocrLines.Count,candidateCount=result.Lines.Where(l=>l.Source=="ocr").Sum(l=>l.Events.Count),acceptedCount=accepted.Count(e=>e.Source is "ocr" or "hybrid")}
                };
                string fingerprint=JsonDefaults.Serialize(new {errors,warnings,uncertain,methods,readings=accepted.Select(e=>new{e.Room,e.Code,e.OccurredAt,e.Source})});
                bool imageDue=cfg.ShareEvidence && evidence!=null && DateTime.UtcNow>=nextImage;
                if(fingerprint!=lastFingerprint || DateTime.UtcNow>=nextHeartbeat || imageDue || found.Any(e=>e.Kind=="event")) {
                    string image=null;
                    if(imageDue) {
                        using var thumb=new Bitmap(evidence,new Size(Math.Min(1280,evidence.Width),Math.Max(1,(int)(evidence.Height*Math.Min(1,1280d/evidence.Width)))));
                        using var ms=new MemoryStream(); using var parameters=new EncoderParameters(1);
                        parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,55L);
                        thumb.Save(ms,ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid),parameters);
                        if(ms.Length<400000) image=Convert.ToBase64String(ms.ToArray());
                        else warnings.Add("IMAGE_TOO_LARGE: 공유 이미지가 큽니다. 로그 영역을 좁혀 주세요");
                        nextImage=DateTime.UtcNow.AddSeconds(5);
                    }
                    var selectedApp=cfg.SelectedApp==null?null:new {name=cfg.SelectedApp.Name,process=Path.GetFileNameWithoutExtension(cfg.SelectedApp.Executable),title=candidates.FirstOrDefault()?.Title??cfg.SelectedApp.Title,method="explicit-selection"};
                    var observation=new {
                        capturedAt=DateTimeOffset.UtcNow,source,methods,readerVersion=Updater.Version,application,
                        ocrLanguage=OcrLang,os=Environment.OSVersion.ToString(),desktopAvailable=desktop,
                        remoteSession=SystemInformation.TerminalServerSession,selectedApp,captureMethod,windows=candidates,regions,errors,warnings,
                        uncertainFields=uncertain.Distinct().Take(1000),
                        coverage=new {mode=activeProfile.Mode,suggestedMode=result.SuggestedMode,expectedRooms=activeProfile.ExpectedRooms,observedRooms=accepted.Select(e=>e.Room).Distinct().Take(500),pending=result.Pending,snapshotEvidenceAt=result.SnapshotEvidenceAt},
                        readings=accepted.Take(600).Select(e=>new {e.Room,e.Code,e.Kind,e.RawLine,e.OccurredAt,e.ObservedAt,e.Source}),
                        channelReadings=result.Lines.SelectMany(l=>l.Events.Select(e=>new {e.Room,e.Code,e.Kind,e.RawLine,e.OccurredAt,e.ObservedAt,source=l.Source})).Take(1200),
                        lines=diagLines.Take(100).Select(l=>new {text=l.Text,source=l.Source,code=l.Event?.Code,room=l.Event?.Room,reason=l.Reason}),
                        unmatched=result.Lines.Where(l=>l.Events.Count==0).Take(40).Select(l=>new {text=l.Text[..Math.Min(l.Text.Length,300)],reason=l.Reason}),image
                    };
                    Sink.Enqueue(observation,found,activeProfile.Revision);
                    foreach(var ev in found.Where(e=>e.Kind=="event")) seen[ev.DedupKey]=ev.ObservedAt;
                    if(seen.Count>20000) foreach(var key in seen.OrderBy(x=>x.Value).Take(5000).Select(x=>x.Key).ToArray()) seen.Remove(key);
                    EventsFound+=found.Count; lastFingerprint=fingerprint; nextHeartbeat=DateTime.UtcNow.AddSeconds(5);
                }
                lock(gate) {
                    foreach(var d in diags) d.LastImage?.Dispose(); diags.Clear();
                    diags.Add(new(){Index=0,LastImage=evidence==null?null:(Bitmap)evidence.Clone(),Lines=diagLines,LastOcrAt=DateTime.Now});
                }
            } catch(Exception ex) { Logger.Error("수집 실패: "+ex.Message); LastCaptureStatus="CAPTURE_FAILURE: "+ex.Message;captureReport.Set("CAPTURE","failed",LastCaptureStatus); reader.BreakContinuity(); }
            finally { evidence?.Dispose(); LastCycleAt=DateTime.Now; }
            try { await Task.Delay(activeProfile.PollMs,cts.Token); } catch(OperationCanceledException) { break; }
        }
    }
    public List<(Bitmap Image,List<LineDiag> Lines,DateTime At)> SnapshotDiagnostics()
    { lock(gate) return diags.Select(d=>(d.LastImage==null?null:(Bitmap)d.LastImage.Clone(),new List<LineDiag>(d.Lines),d.LastOcrAt)).ToList(); }
    public void Dispose() { cts.Cancel(); try{loop?.Wait(5000);}catch{} capture.Dispose(); Sink.Dispose(); lock(gate) foreach(var d in diags)d.LastImage?.Dispose(); }
}
