using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace RmsLink;

/// <summary>파싱된 이벤트를 Neon Postgres로 전송. 실패 시 큐에 보관 후 재시도.
/// 모든 이벤트는 로컬 jsonl 파일에도 백업.</summary>
public sealed class NeonSink : IDisposable
{
    private readonly string _connString;
    private readonly string _hotelId;
    private readonly ConcurrentQueue<ParsedEvent> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public long SentCount;
    public long FailCount;
    public string LastError = "";
    public DateTime LastSentAt = DateTime.MinValue;
    public string OcrLangForStatus = "";

    public int PendingCount => _queue.Count;

    public NeonSink(string connString, string hotelId)
    {
        _connString = connString;
        _hotelId = hotelId;
        _loop = Task.Run(FlushLoopAsync);
    }

    public void Enqueue(ParsedEvent ev)
    {
        AppendLocalBackup(ev);
        _queue.Enqueue(ev);
        if (_queue.Count > 20000)
            _queue.TryDequeue(out _); // 폭주 방지
    }

    private void AppendLocalBackup(ParsedEvent ev)
    {
        try
        {
            var path = Path.Combine(AppConfig.Dir, $"events-{DateTime.Now:yyyyMMdd}.jsonl");
            var obj = new
            {
                hotel = _hotelId,
                room = ev.Room,
                code = ev.Code,
                raw = ev.EventRaw,
                time = ev.EventTime,
                date = ev.EventDate.ToString("yyyy-MM-dd"),
                line = ev.RawLine,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            File.AppendAllText(path, JsonSerializer.Serialize(obj) + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private async Task FlushLoopAsync()
    {
        var heartbeatDue = DateTime.MinValue;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_queue.IsEmpty)
                {
                    await using var conn = new NpgsqlConnection(_connString);
                    await conn.OpenAsync(_cts.Token);

                    while (_queue.TryPeek(out var ev))
                    {
                        await using var cmd = new NpgsqlCommand(
                            "insert into rms_events (hotel_id, room, event_code, event_raw, event_time, event_date, raw_line) " +
                            "values (@h, @r, @c, @e, @t, @d, @raw) " +
                            "on conflict on constraint rms_events_dedup do nothing", conn);
                        cmd.Parameters.AddWithValue("h", _hotelId);
                        cmd.Parameters.AddWithValue("r", ev.Room);
                        cmd.Parameters.AddWithValue("c", ev.Code);
                        cmd.Parameters.AddWithValue("e", ev.EventRaw);
                        cmd.Parameters.AddWithValue("t", ev.EventTime);
                        cmd.Parameters.AddWithValue("d", ev.EventDate);
                        cmd.Parameters.AddWithValue("raw", (object)ev.RawLine ?? DBNull.Value);
                        await cmd.ExecuteNonQueryAsync(_cts.Token);

                        _queue.TryDequeue(out _);
                        Interlocked.Increment(ref SentCount);
                        LastSentAt = DateTime.Now;
                    }
                    LastError = "";
                }

                if (DateTime.Now > heartbeatDue)
                {
                    await UpsertHeartbeatAsync();
                    heartbeatDue = DateTime.Now.AddSeconds(60);
                }

                await Task.Delay(3000, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref FailCount);
                LastError = ex.Message;
                Logger.Error("DB 전송 실패: " + ex.Message);
                try { await Task.Delay(15000, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task UpsertHeartbeatAsync()
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(_cts.Token);
        await using var cmd = new NpgsqlCommand(
            "insert into rms_agent_status (hotel_id, last_seen, version, machine, ocr_lang, events_total) " +
            "values (@h, now(), @v, @m, @l, @n) " +
            "on conflict (hotel_id) do update set last_seen = now(), version = @v, machine = @m, ocr_lang = @l, events_total = @n", conn);
        cmd.Parameters.AddWithValue("h", _hotelId);
        cmd.Parameters.AddWithValue("v", "0.1.0");
        cmd.Parameters.AddWithValue("m", Environment.MachineName);
        cmd.Parameters.AddWithValue("l", OcrLangForStatus);
        cmd.Parameters.AddWithValue("n", Interlocked.Read(ref SentCount));
        await cmd.ExecuteNonQueryAsync(_cts.Token);
    }

    /// <summary>연결 테스트 (미리보기 창에서 사용)</summary>
    public static async Task<string> TestConnectionAsync(string connString)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("select count(*) from rms_events", conn);
            var n = await cmd.ExecuteScalarAsync();
            return $"연결 성공 (rms_events 누적 {n}건)";
        }
        catch (Exception ex)
        {
            return "연결 실패: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(2000); } catch { }
        _cts.Dispose();
    }
}
