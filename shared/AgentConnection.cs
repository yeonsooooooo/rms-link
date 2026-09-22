using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace RmsLink.Shared;

public static class AgentConnection
{
    public static async Task Health(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("health", ct);
        await Ensure(response, "SERVER", ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!body.RootElement.TryGetProperty("service", out var service) || service.GetString() != "RmsLink" ||
            !body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new IOException("SERVER_IDENTITY: RmsLink 서버 응답이 아닙니다. 서버 주소와 중계 설정을 확인하세요.");
    }
    public static async Task Enroll(HttpClient http, string deviceId, string code, string machine, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("agent/enroll", new { deviceId, enrollmentCode = code, machine }, ct);
        await Ensure(response, "ENROLL", ct);
        await RequireOk(response, "ENROLL", ct);
    }
    public static async Task RequireOk(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new IOException(stage + "_RESPONSE: 서버 확인 응답이 올바르지 않습니다.");
    }
    public static async Task Ensure(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string code = "", error = "";
        // Never put an arbitrary proxy response (or a reflected credential) into logs.
        try {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
            if (body.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
        } catch (JsonException) { }
        var reason = code switch {
            "ENROLLMENT_EXPIRED" => "설치 등록권이 만료되었습니다. 대시보드의 Windows 설치 파일 → 새 등록 코드 발급 후 입력하세요.",
            "ENROLLMENT_EXHAUSTED" => "설치 등록권 사용 횟수를 모두 사용했습니다. 새 등록 코드를 발급하세요.",
            "ENROLLMENT_INVALID" => "설치 등록 코드가 올바르지 않습니다. 새 등록 코드를 입력하세요.",
            "DEVICE_REVOKED" => "이 PC의 기기 등록이 중지되었습니다. 관리 담당자에게 확인하세요.",
            _ when stage == "ENROLL" && (error??"").StartsWith("설치 등록권 만료:") => "설치 등록권이 만료되었거나 유효하지 않습니다. 새 등록 코드가 필요합니다.",
            _ when response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "기기 인증이 거부되었습니다. 등록 코드와 기기 등록 상태를 확인하세요.",
            _ when (int)response.StatusCode == 429 => "서버 요청이 많습니다. 잠시 후 다시 시도하세요.",
            _ when (int)response.StatusCode >= 500 => "관리 서버 또는 HTTPS 중계가 응답하지 않습니다. 서버 상태를 확인하고 다시 시도하세요.",
            _ => "서버가 요청을 거부했습니다. 서버 주소와 앱 버전을 확인하세요."
        };
        throw new HttpRequestException($"{stage}_HTTP_{(int)response.StatusCode}: {reason}", null, response.StatusCode);
    }
    public static string Describe(Exception ex, string stage) => ex switch {
        OperationCanceledException => stage + "_TIMEOUT: 연결 시간이 초과되었습니다. 인터넷, 방화벽, 서버 상태를 확인한 뒤 다시 시도하세요.",
        HttpRequestException h when h.StatusCode == null => stage + "_NETWORK: 서버에 연결하지 못했습니다. 인터넷·DNS·프록시·HTTPS 인증서와 Windows 시간을 확인하세요.",
        JsonException => stage + "_RESPONSE: 서버 응답 형식이 올바르지 않습니다. HTTPS 중계와 서버 버전을 확인하세요.",
        _ => ex.Message
    };
}
