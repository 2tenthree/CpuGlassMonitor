using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CpuGlassMonitor.Core;
using CpuGlassMonitor.Sensors;
using CpuGlassMonitor.Ui;

namespace CpuGlassMonitor;

public partial class App : Application
{
    private Mutex? _mutex;

    // 用独立配置目录（CPUGLASS_CONFIG_DIR）跑测试实例时，互斥量名也跟着变，
    // 免得测试进程被用户正在用的那个实例挡住。
    private static readonly string MutexName = BuildMutexName();

    public AppSettings Settings { get; private set; } = new();

    public SensorEngine? Engine { get; private set; }

    public MainWindow? Shell { get; private set; }

    public TrayController? Tray { get; private set; }

    public bool IsElevated { get; private set; }

    public static bool NoElevate { get; private set; }
    public static bool FakeTemps { get; private set; }

    public static AppSettings CurrentSettings => Current is App app ? app.Settings : new AppSettings();

    public static void NotifySettingsChanged()
    {
        if (Current is App app) app.ApplySettings(app.Settings);
    }

    public static void ExitCurrent()
    {
        if (Current is App app) app.ExitApp();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        NoElevate = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        bool fakeTemps = e.Args.Any(a => a.Equals("--fake-temps", StringComparison.OrdinalIgnoreCase));
        FakeTemps = fakeTemps;
        bool autostart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        _ = autostart;
        SettingsStore.Log("启动: " + string.Join(' ', e.Args) + " | exe=" + Environment.ProcessPath);

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

        // 单实例检测：不能用「createdNew」判断。上一个实例被强杀（或调试用例）后，
        // 内核对象可能仍被别人打开着，createdNew 会一直是 false，导致「明明没有实例在跑」
        // 却一直提示"已有实例在运行"。这里改为争抢所有权：WaitOne(0) 成功才算拿到。
        _mutex = new Mutex(false, MutexName);
        bool ownsMutex;
        try
        {
            ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出，所有权已转移给我们
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            SettingsStore.Log("检测到已有实例在运行，本次启动退出。");
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        Settings = SettingsStore.Load();
        IsElevated = CheckElevated();

        // 读取 CPU 温度/频率/电压需要管理员权限，因此自动以管理员身份重启自身
        bool relaunched = e.Args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));
        if (!IsElevated && Settings.AskElevation && !NoElevate && !relaunched)
        {
            if (TryRelaunchElevated(e.Args))
            {
                Shutdown(0);
                return;
            }

            SettingsStore.Log("用户取消了管理员权限提升，以降级模式运行。");
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => SettingsStore.Log("未处理异常: " + args.ExceptionObject);

        Engine = new SensorEngine()
        {
            FakeTemps = FakeTemps
        };

        try
        {
            Shell = new MainWindow(Engine, Settings, IsElevated);
            MainWindow = Shell;
            Shell.Show();
        }
        catch (Exception ex)
        {
            SettingsStore.Log("主窗口创建失败: " + ex);
            Shutdown(1);
            return;
        }

        Tray = new TrayController(Shell, Settings);

        int shotIndex = Array.FindIndex(e.Args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        int shotSettingsIndex = Array.FindIndex(e.Args, a => a.Equals("--shot-settings", StringComparison.OrdinalIgnoreCase));
        int shotMenuIndex = Array.FindIndex(e.Args, a => a.Equals("--shot-menu", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0 || shotSettingsIndex >= 0 || shotMenuIndex >= 0)
        {
            int idx = shotMenuIndex >= 0 ? shotMenuIndex : shotIndex >= 0 ? shotIndex : shotSettingsIndex;
            string path = idx + 1 < e.Args.Length ? e.Args[idx + 1] : "shot.png";
            ScheduleCapture(path, shotSettingsIndex >= 0 ? CaptureMode.Settings
                : shotMenuIndex >= 0 ? CaptureMode.Menu
                : CaptureMode.Main);
        }

        // 传感器枚举比较耗时，放到窗口显示之后再做，避免启动白屏
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                Engine.Open();
                Engine.IntervalMs = Settings.RefreshMs;
                Engine.Start();

                if (!Settings.DefaultsApplied || Settings.SelectedKeys.Count == 0)
                {
                    Settings.SelectedKeys = Engine.Catalog.Values
                        .Where(s => s.DefaultOn)
                        .Select(s => s.Key)
                        .ToList();
                    Settings.DefaultsApplied = true;
                    SettingsStore.Save(Settings);
                    Shell?.ApplySettings(Settings);
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("传感器初始化异常: " + ex);
            }

            Shell?.OnEngineReady();
            Tray?.SyncState();
        }));
    }

    private enum CaptureMode
    {
        Main,
        Settings,
        Menu
    }

    /// <summary>调试用：等界面稳定后把窗口自身渲染成 PNG，然后退出（用于无桌面环境的视觉验证）。</summary>
    private void ScheduleCapture(string path, CaptureMode mode)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(mode == CaptureMode.Main ? 7000 : 2600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (mode == CaptureMode.Settings)
                {
                    var window = new SettingsWindow(Engine!, Settings, IsElevated);
                    window.Show();
                    var inner = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                    inner.Tick += (_, _) =>
                    {
                        inner.Stop();
                        try
                        {
                            CaptureVisual(window, path, "settings");
                        }
                        catch (Exception ex)
                        {
                            SettingsStore.Log("设置面板截图失败: " + ex);
                        }

                        Shutdown(0);
                    };
                    inner.Start();
                    return;
                }

                if (mode == CaptureMode.Menu && Shell != null)
                {
                    Shell.OpenContextMenuForTest();
                    var inner = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    inner.Tick += (_, _) =>
                    {
                        inner.Stop();
                        try
                        {
                            CaptureVisual(Shell.Menu, path, "menu");
                        }
                        catch (Exception ex)
                        {
                            SettingsStore.Log("菜单截图失败: " + ex);
                        }

                        Shutdown(0);
                    };
                    inner.Start();
                    return;
                }

                if (Shell != null) CaptureVisual(Shell, path, "main");
            }
            catch (Exception ex)
            {
                SettingsStore.Log("截图失败: " + ex);
            }

            Shutdown(0);
        };

        timer.Start();
    }

    private void CaptureVisual(FrameworkElement visual, string path, string tag)
    {
        visual.UpdateLayout();
        double w = visual.ActualWidth;
        double h = visual.ActualHeight;
        SettingsStore.Log($"shot[{tag}]: size={w}x{h}");
        if (w < 4 || h < 4) return;

        DpiScale scale = VisualTreeHelper.GetDpi(visual);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(w * scale.DpiScaleX),
            (int)Math.Ceiling(h * scale.DpiScaleY),
            96 * scale.DpiScaleX,
            96 * scale.DpiScaleY,
            PixelFormats.Pbgra32);

        var host = new DrawingVisual();
        using (DrawingContext dc = host.RenderOpen())
        {
            var back = new LinearGradientBrush(
                Color.FromRgb(0x2C, 0x45, 0x5E), Color.FromRgb(0x3A, 0x24, 0x38), 40);
            dc.DrawRectangle(back, null, new Rect(0, 0, w, h));
        }

        rtb.Render(host);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (FileStream fs = File.Create(full)) encoder.Save(fs);
        SettingsStore.Log("shot: saved " + full);
    }

    /// <summary>无界面自检：打开传感器采样若干次，把结果写入日志后退出（用于自动化验证）。</summary>
    private void RunSelfTest()
    {
        var lines = new List<string>();
        try
        {
            IsElevated = CheckElevated();
            lines.Add("elevated=" + IsElevated);
            Settings = SettingsStore.Load();

            Engine = new SensorEngine()
            {
                FakeTemps = FakeTemps
            };
            Engine.Open();
            lines.Add($"open=ok cpu={Engine.CpuName} gpu={Engine.GpuName} catalog={Engine.Catalog.Count} cpuTempAvailable={Engine.CpuTemperatureAvailable} error={Engine.Error ?? "-"}");

            SensorReading? last = null;
            Engine.Updated += r => last = r;
            Engine.IntervalMs = 400;
            Engine.Start();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && last == null) Thread.Sleep(100);
            Thread.Sleep(800);

            if (last == null)
            {
                lines.Add("reading=NONE");
            }
            else
            {
                foreach (var pair in Engine.Catalog.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    double? v = last.Get(pair.Key);
                    lines.Add($"{pair.Key} = {(v.HasValue ? pair.Value.Format(v.Value) + pair.Value.Unit : "n/a")}");
                }
            }

            Engine.Dispose();
        }
        catch (Exception ex)
        {
            lines.Add("SELFTEST EXCEPTION: " + ex);
        }

        foreach (string line in lines) SettingsStore.Log("[selftest] " + line);
        Shutdown(0);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SettingsStore.Log("UI 线程异常: " + e.Exception);
        e.Handled = true;
    }

    public void ApplySettings(AppSettings updated, bool persist = true)
    {
        Settings = updated;
        if (persist) SettingsStore.Save(updated);
        Shell?.ApplySettings(updated);
        Tray?.SyncState();
    }

    /// <summary>设置面板里的实时预览：只改界面，不落盘。</summary>
    public void PreviewSettings(AppSettings preview) => Shell?.ApplySettings(preview, rebuildRows: true);

    public void ExitApp()
    {
        try
        {
            Engine?.Dispose();
            Tray?.Dispose();
        }
        catch
        {
            // 退出路径上的异常不再处理
        }

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 忽略：可能未持有互斥量
        }

        Shutdown(0);
    }

    private static string BuildMutexName()
    {
        string scope = Environment.GetEnvironmentVariable("CPUGLASS_CONFIG_DIR") ?? string.Empty;

        if (scope.Length == 0)
        {
            return @"Global\CpuGlassMonitor.SingleInstance";
        }

        // FNV-1a：把配置目录揉成一个短后缀，保证同一目录只允许一个实例
        uint hash = 2166136261;
        foreach (char ch in scope.ToLowerInvariant())
        {
            hash = (hash ^ ch) * 16777619;
        }

        return @"Global\CpuGlassMonitor.SingleInstance." + hash.ToString("x8");
    }

    private static bool CheckElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            foreach (string a in args)
            {
                if (a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)) continue;
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("--elevated");
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log("提权失败: " + ex.Message);
            return false;
        }
    }
}
