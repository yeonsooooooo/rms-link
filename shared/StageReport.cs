using System.Text.Json;
namespace RmsLink.Shared;

// A small, bounded report survives a failed install and never stores credentials.
public sealed class StageReport
{
    readonly string path;
    readonly object gate = new();
    readonly Dictionary<string, object> steps = new();
    readonly string runId = Guid.NewGuid().ToString();
    public string Stage { get; private set; } = "START";
    public string Status { get; private set; } = "waiting";
    public StageReport(string path) { this.path = path; }
    public void Set(string stage, string status, string message)
    {
        lock (gate)
        {
            Stage = stage; Status = status;
            steps[stage] = new { stage, status, message, at = DateTimeOffset.UtcNow };
            var text = JsonSerializer.Serialize(new { runId, stage, status, at = DateTimeOffset.UtcNow, steps = steps.Values }, new JsonSerializerOptions { WriteIndented = true });
            try { Write(path, text); }
            catch { try { Write(Path.Combine(Path.GetTempPath(), "RmsLink-" + Path.GetFileName(path)), text); } catch { } }
        }
    }
    static void Write(string target, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        File.WriteAllText(target + ".tmp", text);
        File.Move(target + ".tmp", target, true);
    }
}
