using System.Text;

namespace RmsLink;

public static class Logger
{
    private static readonly object Gate = new();

    private static string LogPath =>
        Path.Combine(AppConfig.Dir, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss} [{level}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* 로깅 실패는 무시 */ }
    }
}
