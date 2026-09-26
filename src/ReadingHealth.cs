namespace RmsLink;

public sealed record ReadingHealth(string Code,string Message,bool NeedsAttention,int TextLines,int Candidates,int Accepted)
{
    public static ReadingHealth Starting {get;}=new("STARTING","첫 화면을 기다리는 중",false,0,0,0);
    public static ReadingHealth Evaluate(ReadingResult result,IReadOnlyList<ParsedEvent> accepted,
        IReadOnlyList<string> errors,IReadOnlyList<string> warnings,DateTimeOffset now)
    {
        int lines=result.Lines.Count,candidates=result.Lines.Sum(l=>l.Events.Count);
        ReadingHealth Status(string code,string message,bool attention=true) => new(code,message,attention,lines,candidates,accepted.Count);
        if(errors.Count>0) {
            if(errors.Any(e=>e.StartsWith("PARSE_NO_MATCH"))) {
                if(warnings.Any(w=>w.StartsWith("MODE_CONFIRMATION_REQUIRED")))
                    return Status("MODE_CONFIRMATION_REQUIRED","시각 없는 문·키 상태를 읽었습니다. 대시보드의 화면·객실 설정에서 현재 상태표인지 확인하세요");
                if(result.Lines.Any(l=>l.Reason.StartsWith("어휘 불일치")))
                    return Status("UNMATCHED_ROWS","객실·시각은 읽었지만 상태 문구를 해석하지 못했습니다. 대시보드에서 원문·실제 상태 샘플을 등록하세요");
                return Status("PARSE_NO_MATCH","글자는 읽었지만 객실·시각·문/키 상태가 한 행에 모이지 않았습니다. 이벤트 내역 창을 선택하고 필요한 열이 보이는지 확인하세요");
            }
            return Status(errors[0].Split(':')[0],errors[0]);
        }
        if(accepted.Count>0) {
            if(accepted.All(e=>e.Kind=="event" && now-e.OccurredAt>=TimeSpan.FromMinutes(5)))
                return Status("HISTORY_ONLY","과거 이벤트만 읽었습니다. 시험 객실에서 문·키를 조작하고 새 내역과 수신 값을 대조하세요");
            bool partial=warnings.Any(w=>w.StartsWith("READING_CONFLICT") || w.StartsWith("LAYOUT_AMBIGUOUS") || w.StartsWith("ROOM_NOT_ALLOWED") || w.StartsWith("UNMATCHED_ROWS"));
            return Status(partial?"PARTIAL_READING":"READING_READY",partial?"일부 행은 확인이 필요합니다. 확인된 객실 상태는 계속 전송합니다":"문·키 상태 판독 확인 · 실제 동작과 대조 필요",partial);
        }
        if(result.UncertainFields.Count>0 && warnings.Any(w=>w.StartsWith("READING_CONFLICT") || w.StartsWith("LAYOUT_AMBIGUOUS")))
            return Status("READING_CONFLICT","객실 번호 또는 상태가 서로 달라 반영을 보류했습니다. 판독 미리보기에서 원문과 화면을 대조하세요");
        if(warnings.Any(w=>w.StartsWith("SCREEN_FRESHNESS_UNCONFIRMED")))
            return Status("SCREEN_FRESHNESS_UNCONFIRMED","상태표의 갱신을 확인할 수 없습니다. RMS 갱신 상태와 화면 설정을 확인하세요");
        if(result.Pending>0)return Status("OCR_CONFIRMING","OCR 결과를 다음 화면과 대조 중 · 계속 대기하면 글자 크기와 로그 영역을 확인하세요");
        return Status("READING_WAITING","반영할 문·키 상태가 없습니다. 이벤트 내역과 판독 미리보기를 확인하세요");
    }
}

// UI notifications are delayed for transient failures and throttled independently of changing counts.
public sealed class ReadingAlert
{
    DateTimeOffset? since;
    DateTimeOffset? lastAlert;
    public bool ShouldNotify(bool attention,DateTimeOffset now)
    {
        if(!attention){since=null;lastAlert=null;return false;}
        since??=now;
        if(now-since.Value<TimeSpan.FromSeconds(15) || (lastAlert.HasValue && now-lastAlert.Value<TimeSpan.FromMinutes(10)))return false;
        lastAlert=now;return true;
    }
}
