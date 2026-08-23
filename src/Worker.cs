namespace RmsLink;

public sealed class LineDiag
{
    public string Text;
    public ParsedEvent Event;   // null이면 미매칭
    public string Reason;       // 미매칭 사유
    public bool IsNew;          // 이번 사이클에 새로 발견되어 전송된 것
}

public sealed class RegionDiag
{
    public int Index;
    public Bitmap LastImage;    // 마지막 캡처 (미리보기용)
    public List<LineDiag> Lines = new();
    public DateTime LastOcrAt = DateTime.MinValue;
}

/// <summary>캡처 → 변화감지 → OCR → 파싱 → 전송 루프</summary>
public sealed class Worker : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly OcrService _ocr;
    public readonly NeonSink Sink;

    private readonly CancellationTokenSource _cts = new();
    private Task _loop;

    private readonly ulong[] _lastHash;
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private const int SeenCap = 5000;

    private readonly object _diagGate = new();
    private readonly List<RegionDiag> _diags = new();

    public long OcrRuns;
    public long EventsFound;
    public DateTime LastCycleAt = DateTime.MinValue;
    public string OcrLang => _ocr?.LanguageTag ?? "(없음)";

    public Worker(AppConfig cfg, OcrService ocr)
    {
        _cfg = cfg;
        _ocr = ocr;
        Sink = new NeonSink(cfg.ConnString, cfg.HotelId) { OcrLangForStatus = ocr?.LanguageTag ?? "none" };
        _lastHash = new ulong[cfg.Regions.Count];
        for (int i = 0; i < cfg.Regions.Count; i++)
            _diags.Add(new RegionDiag { Index = i });
    }

    public void Start()
    {
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        Logger.Info($"워커 시작: 영역 {_cfg.Regions.Count}개, 주기 {_cfg.IntervalMs}ms, OCR={OcrLang}");
        while (!_cts.IsCancellationRequested)
        {
            var started = DateTime.Now;
            for (int i = 0; i < _cfg.Regions.Count; i++)
            {
                try { await ProcessRegionAsync(i); }
                catch (Exception ex) { Logger.Error($"영역{i + 1} 처리 오류: " + ex.Message); }
            }
            LastCycleAt = DateTime.Now;

            var elapsed = (int)(DateTime.Now - started).TotalMilliseconds;
            int wait = Math.Max(200, _cfg.IntervalMs - elapsed);
            try { await Task.Delay(wait, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessRegionAsync(int i)
    {
        var rect = _cfg.Regions[i].Rect;
        Bitmap bmp = CaptureService.Capture(rect);

        ulong h = CaptureService.QuickHash(bmp);
        if (h == _lastHash[i])
        {
            bmp.Dispose();
            return; // 변화 없음 → OCR 생략
        }
        _lastHash[i] = h;

        var lines = await _ocr.ReadLinesAsync(bmp, _cfg.OcrScale);
        Interlocked.Increment(ref OcrRuns);

        var now = DateTime.Now;
        var diagLines = new List<LineDiag>();

        foreach (var line in lines)
        {
            var ev = EventParser.TryParse(line, now, out var reason);
            bool isNew = false;
            if (ev != null && MarkSeen(ev.DedupKey))
            {
                isNew = true;
                Interlocked.Increment(ref EventsFound);
                Sink.Enqueue(ev);
            }
            diagLines.Add(new LineDiag { Text = line, Event = ev, Reason = reason, IsNew = isNew });
        }

        lock (_diagGate)
        {
            var d = _diags[i];
            d.LastImage?.Dispose();
            d.LastImage = bmp; // 소유권 이전
            d.Lines = diagLines;
            d.LastOcrAt = now;
        }
    }

    private bool MarkSeen(string key)
    {
        lock (_seen)
        {
            if (_seen.Contains(key)) return false;
            _seen.Add(key);
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > SeenCap)
                _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    /// <summary>미리보기용 스냅샷 (이미지는 복제본)</summary>
    public List<(Bitmap Image, List<LineDiag> Lines, DateTime At)> SnapshotDiagnostics()
    {
        lock (_diagGate)
        {
            var list = new List<(Bitmap, List<LineDiag>, DateTime)>();
            foreach (var d in _diags)
                list.Add((d.LastImage != null ? (Bitmap)d.LastImage.Clone() : null,
                          new List<LineDiag>(d.Lines), d.LastOcrAt));
            return list;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        Sink.Dispose();
        _cts.Dispose();
        lock (_diagGate)
            foreach (var d in _diags) d.LastImage?.Dispose();
    }
}
