namespace RmsLink;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Contains("--capture-fixture"))return Diagnostics.CaptureFixture();
        if(args.Length==2 && args[0]=="--runtime-test")return Diagnostics.RuntimeTest(args[1],true);
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

        int waitAt=Array.IndexOf(args,"--wait-pid");
        if(waitAt>=0 && waitAt+1<args.Length && int.TryParse(args[waitAt+1],out int parentId))try{using var parent=System.Diagnostics.Process.GetProcessById(parentId);if(!parent.WaitForExit(45000))return 1;}catch(ArgumentException){}

        // ---- 일반 GUI 모드 ----
        try
        {
            return RunGui(args);
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

    private static int RunGui(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\RmsLinkSingleton", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RmsLink가 이미 실행 중입니다.\n트레이(작업표시줄 우측 하단) 아이콘을 확인하세요.",
                "RmsLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) WriteCrash(ex);
            else Logger.Error("치명적 오류: " + e.ExceptionObject);
        };
        Application.ThreadException += (_, e) => WriteCrash(e.Exception);

        Logger.Info("===== RmsLink 시작 =====");
        Logger.Info($"OS={Environment.OSVersion}, .NET={Environment.Version}, exe={Environment.ProcessPath}");

        var cfg = AppConfig.Load();
        bool resume=args.Contains("--resume") && !string.IsNullOrWhiteSpace(cfg.HotelId);
        if (!resume) {
            using var setup = new SetupForm(cfg);
            if (setup.ShowDialog() != DialogResult.OK) return 0;
        }
        // The diagnostic transport must remain alive even when OCR is unavailable.
        OcrService ocr=null;
        try { ocr=OcrService.Create(); } catch(Exception ex){Logger.Error("OCR 초기화: "+ex.Message);}
        Directory.CreateDirectory(AppConfig.InstallDir);
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
