using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RmsLink;

public static class Diagnostics
{
    /// <summary>--selftest: OCR/DB/화면 자가진단. 리포트를 파일+stdout에 기록.
    /// OCR 사용 가능하면 exit 0, 불가하면 2.</summary>
    public static int RunSelfTest()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        Directory.CreateDirectory(AppConfig.Dir);
        W("===== RmsLink 자체 점검 =====");
        W($"시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"버전: {typeof(Diagnostics).Assembly.GetName().Version}");
        W($"머신: {Environment.MachineName} / 사용자: {Environment.UserName}");
        W($"OS: {Environment.OSVersion}  64bit={Environment.Is64BitOperatingSystem}");
        W($".NET: {Environment.Version}");
        W($"실행 경로: {Environment.ProcessPath}");
        W($"데이터 폴더: {AppConfig.Dir}");

        // 화면
        try
        {
            var vs = SystemInformation.VirtualScreen;
            W($"가상화면(다중모니터): X={vs.X} Y={vs.Y} W={vs.Width} H={vs.Height}");
        }
        catch (Exception ex) { W("화면 정보 오류: " + ex.Message); }

        bool ocrOk = false;
        // OCR
        try
        {
            var langs = OcrService.AvailableLanguages();
            W($"설치된 OCR 언어: {(langs.Count == 0 ? "(없음)" : string.Join(", ", langs))}");
            bool hasKo = langs.Any(l => l.StartsWith("ko", StringComparison.OrdinalIgnoreCase));
            W($"한국어 OCR 언어팩: {(hasKo ? "설치됨 ✅" : "미설치 ⚠️  (설정>시간 및 언어>언어>한국어)")}");

            var ocr = OcrService.Create();
            if (ocr == null)
                W("OCR 엔진 생성: 실패 ❌ (Windows OCR 사용 불가)");
            else
            {
                ocrOk = true;
                W($"OCR 엔진 생성: 성공 ✅  사용 언어={ocr.LanguageTag}");
            }
        }
        catch (Exception ex) { W("OCR 점검 오류: " + ex.Message); }

        // 설정
        var cfg = AppConfig.Load();
        W($"설정: hotelId='{cfg.HotelId}', 로그영역 {cfg.Regions.Count}개, 주기 {cfg.IntervalMs}ms, 배율 {cfg.OcrScale}");
        W($"DB 접속정보 설정됨: {(cfg.DbConfigured ? "예" : "아니오")}");

        // DB
        if (cfg.DbConfigured)
        {
            try
            {
                var msg = NeonSink.TestConnectionAsync(cfg.EffectiveConnString())
                            .GetAwaiter().GetResult();
                W("DB 연결 테스트: " + msg);
            }
            catch (Exception ex) { W("DB 연결 테스트 오류: " + ex.Message); }
        }
        else
        {
            W("DB 연결 테스트: 건너뜀 (접속정보 없음)");
        }

        W("결과: " + (ocrOk ? "OCR 사용 가능 → 정상" : "OCR 사용 불가 → 조치 필요"));
        W("=============================");

        try
        {
            File.WriteAllText(Path.Combine(AppConfig.Dir, "selftest-report.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch { }

        return ocrOk ? 0 : 2;
    }

    /// <summary>%APPDATA%\RmsLink 전체를 (비밀번호 마스킹하여) 바탕화면에 zip으로 내보낸다.
    /// 성공 시 zip 경로 반환.</summary>
    public static string ExportDiagnostics()
    {
        var src = AppConfig.Dir;
        Directory.CreateDirectory(src);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var outPath = Path.Combine(desktop, $"RmsLink-진단-{DateTime.Now:yyyyMMdd-HHmm}.zip");

        var tmp = Path.Combine(Path.GetTempPath(), "rmslink-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(f);
                var dest = Path.Combine(tmp, name);
                if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                {
                    // 비밀번호 마스킹
                    try
                    {
                        var text = File.ReadAllText(f);
                        text = Regex.Replace(text, "(?i)(password=)[^;\"\\\\]+", "$1***");
                        File.WriteAllText(dest, text);
                    }
                    catch { File.Copy(f, dest, true); }
                }
                else
                {
                    File.Copy(f, dest, true);
                }
            }

            // 시스템 요약 추가
            var info = new StringBuilder();
            info.AppendLine($"내보낸 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            info.AppendLine($"머신: {Environment.MachineName}");
            info.AppendLine($"OS: {Environment.OSVersion}");
            info.AppendLine($"OCR 언어: {string.Join(", ", OcrService.AvailableLanguages())}");
            info.AppendLine($"실행 경로: {Environment.ProcessPath}");
            File.WriteAllText(Path.Combine(tmp, "system-info.txt"), info.ToString(), Encoding.UTF8);

            if (File.Exists(outPath)) File.Delete(outPath);
            ZipFile.CreateFromDirectory(tmp, outPath);
            return outPath;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
