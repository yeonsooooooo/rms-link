using System.Drawing.Imaging;
namespace RmsLink;
public sealed class LineDiag {public string Text;public ParsedEvent Event;public string Reason;public bool IsNew;}
public sealed class RegionDiag {public int Index;public Bitmap LastImage;public List<LineDiag> Lines=new();public DateTime LastOcrAt=DateTime.MinValue;}
public sealed class Worker:IDisposable
{
    readonly AppConfig cfg;readonly OcrService ocr;readonly CancellationTokenSource cts=new();Task loop;
    readonly object gate=new();readonly List<RegionDiag> diags=new();readonly Dictionary<string,DateTimeOffset> seen=new();readonly WindowCapture capture=new();
    AdapterProfile profile;Task<List<string>> accessibilityTask;long accessibilityHandle;DateTime lastAccessibilityStart=DateTime.MinValue;
    public readonly ControlSink Sink;public long OcrRuns,EventsFound;public DateTime LastCycleAt=DateTime.MinValue;public string OcrLang=>ocr?.LanguageTag??"없음";
    public Worker(AppConfig cfg,OcrService ocr){this.cfg=cfg;this.ocr=ocr;profile=AdapterProfile.Load(cfg.HotelId);Sink=new(cfg){ProfileRevision=profile.Revision};Sink.ProfileChanged=p=>{profile=p;};}
    public void Start()=>loop=Task.Run(Loop);
    async Task Loop()
    {
        DateTime nextHeartbeat=DateTime.MinValue,nextImage=DateTime.MinValue;string lastFingerprint="";
        while(!cts.IsCancellationRequested) {
            var errors=new List<string>();var lines=new List<string>();var candidates=new List<WindowCandidate>();
            var activeProfile=profile;string source="none",captureMethod="none";Bitmap evidence=null;bool desktop=false;
            var regions=cfg.RegionsRelative?cfg.Regions.ToList():new List<CaptureRegion>();
            try {
                desktop=WindowProbe.DesktopAvailable();
                if(!desktop)errors.Add("DESKTOP_LOCKED: Windows에 로그인하고 키텍 창을 표시하세요");
                if(cfg.SelectedApp==null)errors.Add("APP_NOT_SELECTED: 바탕화면 아이콘에서 키텍 앱을 선택하세요");
                else candidates=WindowProbe.Find(cfg.SelectedApp);
                var window=candidates.Count==1?candidates[0]:null;
                if(cfg.SelectedApp!=null&&window==null)errors.Add(candidates.Count>1?"WINDOW_AMBIGUOUS: 같은 앱의 여러 창 중 키텍 창을 다시 선택하세요":"WINDOW_NOT_FOUND: 선택한 키텍 앱을 실행하세요");
                if(window?.Minimized==true)errors.Add("WINDOW_MINIMIZED: 선택한 키텍 창을 복원하세요");
                if(desktop&&window!=null&&!window.Minimized) {
                    // A hung provider consumes at most one task. Results are never reused past 3 seconds.
                    if(regions.Count==0) {
                        if(accessibilityTask==null || (accessibilityTask.IsCompleted&&DateTime.UtcNow-lastAccessibilityStart>TimeSpan.FromSeconds(3))) {
                            _=accessibilityTask?.Exception;accessibilityHandle=window.Handle;lastAccessibilityStart=DateTime.UtcNow;
                            accessibilityTask=Task.Run(()=>WindowProbe.ReadAccessibleRows(window));
                        }
                        if(accessibilityTask?.IsCompletedSuccessfully==true&&accessibilityHandle==window.Handle&&DateTime.UtcNow-lastAccessibilityStart<TimeSpan.FromSeconds(3)) {
                            var accessible=accessibilityTask.Result;
                            if(accessible.Any(l=>EventParser.Parse(l,DateTimeOffset.Now,activeProfile,out _).Count>0)){lines=accessible;source="uia";}
                        }
                        if(accessibilityTask?.IsFaulted==true)errors.Add("UIA_UNAVAILABLE: 화면 OCR로 대체");
                        if(accessibilityTask!=null&&!accessibilityTask.IsCompleted&&DateTime.UtcNow-lastAccessibilityStart>TimeSpan.FromSeconds(6))errors.Add("UIA_TIMEOUT: 화면 OCR로 대체");
                    }
                    try {
                        var captured=await capture.Read(window);using var whole=captured.Image;captureMethod=captured.Method;
                        if(regions.Count==0)evidence=(Bitmap)whole.Clone();
                        else {
                            var region=regions[0].Rect;
                            if(!new Rectangle(0,0,whole.Width,whole.Height).Contains(region)||region.Width<20||region.Height<20)throw new Exception("REGION_INVALID: 키텍 창 크기가 변경되었습니다. 로그 영역을 다시 지정하세요");
                            evidence=whole.Clone(region,PixelFormat.Format32bppArgb);
                        }
                        if(source!="uia") {
                            source="ocr";
                            if(ocr==null)errors.Add("OCR_MISSING: 한국어 Windows OCR 언어팩 필요");
                            else {if(!OcrLang.StartsWith("ko"))errors.Add("OCR_KOREAN_MISSING: 한국어 Windows OCR 언어팩 필요");lines=await ocr.ReadLinesAsync(evidence,activeProfile.OcrScale);Interlocked.Increment(ref OcrRuns);}
                        }
                    }catch(Exception ex){errors.Add(ex.Message.Contains(':')?ex.Message:"CAPTURE_FAILURE: "+ex.Message);}
                }
                var found=new List<ParsedEvent>();var diagLines=new List<LineDiag>();var unmatched=new List<object>();var now=DateTimeOffset.Now;
                foreach(var line in lines.Take(300)) {
                    var text=line[..Math.Min(line.Length,1000)];var parsed=EventParser.Parse(text,now,activeProfile,out var reason);bool fresh=false;
                    foreach(var ev in parsed)if(ev.Kind=="snapshot"||!seen.ContainsKey(ev.DedupKey)){found.Add(ev);fresh=true;}
                    if(parsed.Count==0&&unmatched.Count<40)unmatched.Add(new{text=text[..Math.Min(text.Length,300)],reason});
                    diagLines.Add(new(){Text=text,Event=parsed.FirstOrDefault(),Reason=reason,IsNew=fresh});
                }
                if(lines.Count==0&&source!="none")errors.Add("NO_TEXT: 키텍 로그 영역과 Windows OCR 언어팩을 확인하세요");
                if(lines.Count>0&&!diagLines.Any(l=>l.Event!=null))errors.Add("PARSE_NO_MATCH: 객실·시각·이벤트 어휘 확인 필요");
                string fingerprint=string.Join("|",errors)+string.Join("|",lines);
                bool imageDue=cfg.ShareEvidence&&evidence!=null&&DateTime.UtcNow>=nextImage;
                if(fingerprint!=lastFingerprint||DateTime.UtcNow>=nextHeartbeat||imageDue||found.Any(e=>e.Kind=="event")) {
                    string image=null;
                    if(imageDue) {
                        using var thumb=new Bitmap(evidence,new Size(Math.Min(1280,evidence.Width),Math.Max(1,(int)(evidence.Height*Math.Min(1,1280d/evidence.Width)))));
                        using var ms=new MemoryStream();using var parameters=new EncoderParameters(1);parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,55L);
                        thumb.Save(ms,ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid),parameters);
                        if(ms.Length<400000)image=Convert.ToBase64String(ms.ToArray());else errors.Add("CAPTURE_FAILURE: 이미지가 너무 큽니다. 로그 영역을 좁혀 주세요");
                        nextImage=DateTime.UtcNow.AddSeconds(5);
                    }
                    var selectedApp=cfg.SelectedApp==null?null:new{name=cfg.SelectedApp.Name,process=Path.GetFileNameWithoutExtension(cfg.SelectedApp.Executable),title=candidates.FirstOrDefault()?.Title??cfg.SelectedApp.Title,method="explicit-selection"};
                    var observation=new {capturedAt=DateTimeOffset.UtcNow,source,ocrLanguage=OcrLang,os=Environment.OSVersion.ToString(),desktopAvailable=desktop,remoteSession=SystemInformation.TerminalServerSession,selectedApp,captureMethod,windows=candidates,regions,errors,lines=diagLines.Take(100).Select(l=>new{text=l.Text,code=l.Event?.Code,room=l.Event?.Room,reason=l.Reason}),unmatched,image};
                    Sink.Enqueue(observation,found.Take(600).ToList());
                    foreach(var ev in found.Where(e=>e.Kind=="event"))seen[ev.DedupKey]=ev.ObservedAt;
                    if(seen.Count>20000)foreach(var key in seen.OrderBy(x=>x.Value).Take(5000).Select(x=>x.Key).ToArray())seen.Remove(key);
                    EventsFound+=found.Count;lastFingerprint=fingerprint;nextHeartbeat=DateTime.UtcNow.AddSeconds(5);
                }
                lock(gate){foreach(var d in diags)d.LastImage?.Dispose();diags.Clear();diags.Add(new(){Index=0,LastImage=evidence==null?null:(Bitmap)evidence.Clone(),Lines=diagLines,LastOcrAt=DateTime.Now});}
            }catch(Exception ex){Logger.Error("수집 실패: "+ex.Message);Sink.LastError=ex.Message;}
            finally{evidence?.Dispose();LastCycleAt=DateTime.Now;}
            try{await Task.Delay(activeProfile.PollMs,cts.Token);}catch(OperationCanceledException){break;}
        }
    }
    public List<(Bitmap Image,List<LineDiag> Lines,DateTime At)> SnapshotDiagnostics(){lock(gate)return diags.Select(d=>(d.LastImage==null?null:(Bitmap)d.LastImage.Clone(),new List<LineDiag>(d.Lines),d.LastOcrAt)).ToList();}
    public void Dispose(){cts.Cancel();try{loop?.Wait(5000);}catch{}capture.Dispose();Sink.Dispose();lock(gate)foreach(var d in diags)d.LastImage?.Dispose();}
}
