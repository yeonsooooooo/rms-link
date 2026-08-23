namespace RmsLink;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // ---- CLI 모드 (UI 없이 실행, 설치 스크립트/원격 진단용) ----
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return Safe(() => Diagnostics.RunSelfTest(), 2);

        if (args.Any(a => a.Equals("--collectlogs", StringComparison.OrdinalIgnoreCase)))
            return Safe(() =>
            {
                var p = Diagnostics.ExportDiagnostics();
                Console.WriteLine("진단 파일 생성: " + p);
                return 0;
            }, 1);

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0");
            return 0;
        }

        // ---- 일반 GUI 모드 ----
        try
        {
            return RunGui();
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            try
            {
                MessageBox.Show(
                    "RmsLink 시작 중 오류가 발생했습니다.\n\n" + ex.Message +
                    "\n\n자세한 내용은 아래 폴더의 crash 로그를 확인하세요:\n" + AppConfig.Dir,
                    "RmsLink 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            return 1;
        }
    }

    private static int RunGui()
    {
        using var mutex = new Mutex(true, @"Global\RmsLinkSingleton", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RmsLink가 이미 실행 중입니다.\n트레이(작업표시줄 우측 하단) 아이콘을 확인하세요.",
                "RmsLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) WriteCrash(ex);
            else Logger.Error("치명적 오류: " + e.ExceptionObject);
        };
        Application.ThreadException += (_, e) => WriteCrash(e.Exception);

        Logger.Info("===== RmsLink 시작 =====");
        Logger.Info($"OS={Environment.OSVersion}, .NET={Environment.Version}, exe={Environment.ProcessPath}");

        var ocr = OcrService.Create();
        if (ocr == null)
        {
            Logger.Error("OCR 엔진 생성 실패");
            MessageBox.Show(
                "이 PC에서 Windows OCR 엔진을 사용할 수 없습니다.\n" +
                "Windows 10 이상 + 한국어 언어팩이 필요합니다.\n\n" +
                "[설정 > 시간 및 언어 > 언어]에서 '한국어'가 설치되어 있는지 확인해 주세요.\n" +
                "설치 후에도 안 되면 '로그 폴더'의 selftest 리포트를 보내주세요.",
                "RmsLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        if (ocr.LanguageTag != "ko")
        {
            Logger.Error("한국어 OCR 없음, 사용 언어=" + ocr.LanguageTag);
            MessageBox.Show(
                $"한국어 OCR을 찾지 못해 '{ocr.LanguageTag}' 언어로 동작합니다.\n" +
                "한글 이벤트(문열림 등) 인식률이 낮을 수 있습니다.\n" +
                "[설정 > 시간 및 언어 > 언어]에서 한국어 언어팩을 설치하면 해결됩니다.",
                "RmsLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var cfg = AppConfig.Load();
        if (string.IsNullOrWhiteSpace(cfg.HotelId) || cfg.Regions.Count == 0)
        {
            using var setup = new SetupForm(cfg);
            if (setup.ShowDialog() != DialogResult.OK)
            {
                Logger.Info("설정 취소로 종료");
                return 0;
            }
        }

        if (!cfg.DbConfigured)
            Logger.Error("경고: DB 접속정보 없음 - 이벤트가 로컬 백업(jsonl)에만 저장됩니다.");

        Logger.Info($"설정: hotel={cfg.HotelId}, 영역 {cfg.Regions.Count}개, OCR={ocr.LanguageTag}, DB={cfg.DbConfigured}");
        Application.Run(new TrayContext(cfg, ocr));
        Logger.Info("===== RmsLink 종료 =====");
        return 0;
    }

    private static int Safe(Func<int> f, int failCode)
    {
        try { return f(); }
        catch (Exception ex) { WriteCrash(ex); Console.WriteLine("오류: " + ex); return failCode; }
    }

    private static void WriteCrash(Exception ex)
    {
        Logger.Error("CRASH: " + ex);
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n";
        // APPDATA와 TEMP 양쪽에 기록 (APPDATA 쓰기 실패 대비)
        foreach (var dir in new[] { AppConfig.Dir, Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"RmsLink-crash-{DateTime.Now:yyyyMMdd}.log"), text);
            }
            catch { }
        }
    }
}
