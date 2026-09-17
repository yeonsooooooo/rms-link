using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace RmsLink.Shared;

public static class UpdateActivation
{
    public static void ValidateVersion(string value)
    { if(!Regex.IsMatch(value??"",@"^\d+\.\d+\.\d+$"))throw new Exception("버전 정보 오류"); }
    public static string ReadCurrent(string root)
    { var value=File.ReadAllText(Path.Combine(root,"current.txt")).Trim();ValidateVersion(value);return value; }
    public static void WriteCurrent(string root,string value)
    { ValidateVersion(value);File.WriteAllText(Path.Combine(root,"current.txt.tmp"),value);File.Move(Path.Combine(root,"current.txt.tmp"),Path.Combine(root,"current.txt"),true); }

    public static bool Apply(string root,Func<string,Process> launch,TimeSpan timeout)
    {
        var pending=Path.Combine(root,"pending.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(pending));
        var next=doc.RootElement.GetProperty("version").GetString();
        // A power loss can leave current.txt already pointing to the unproven version.
        var previous=doc.RootElement.TryGetProperty("previous",out var prior)?prior.GetString():ReadCurrent(root);
        ValidateVersion(next);ValidateVersion(previous);
        if(next==previous)throw new Exception("업데이트 이전 버전 정보 오류");
        var marker=Path.Combine(root,"healthy-"+next);
        Process child=null;
        try {
            File.Delete(marker);WriteCurrent(root,next);
            child=launch(next)??throw new Exception("새 버전 실행 실패");
            var due=Stopwatch.StartNew();
            while(due.Elapsed<timeout && !child.HasExited && !File.Exists(marker))Thread.Sleep(100);
            if(!File.Exists(marker)||child.HasExited)throw new Exception("새 버전 준비 확인 실패");
            File.Delete(Path.Combine(root,"failed-update.json"));File.Delete(pending);return true;
        } catch(Exception ex) {
            if(child!=null&&!child.HasExited){child.Kill(true);if(!child.WaitForExit(5000))throw new Exception("새 버전 종료 실패",ex);}
            WriteCurrent(root,previous);
            File.WriteAllText(Path.Combine(root,"failed-update.json"),JsonSerializer.Serialize(new {version=next,at=DateTimeOffset.UtcNow,error=ex.Message+" · 이전 버전 복구"}));
            File.Delete(pending);
            using var restored=launch(previous);
            return false;
        } finally {child?.Dispose();}
    }
}
