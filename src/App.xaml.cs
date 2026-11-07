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
    private DispatcherTimer? _driverWatch;
    private int _driverRestarts;
    private bool _diagnosed;
    private bool _startedByAutostart;

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

    /// <summary>--dump-cpu：自检时把 CPU 的每个传感器（含 null 值）都写进日志。</summary>
    public static bool DumpCpuSensors { get; private set; }

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

        // --config <目录>：把配置/日志目录改到指定位置（提权实例不继承环境变量，调试时用它）
        int configIndex = Array.FindIndex(e.Args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
        if (configIndex >= 0 && configIndex + 1 < e.Args.Length)
        {
            SettingsStore.OverrideDirectory(e.Args[configIndex + 1]);
        }
        bool fakeTemps = e.Args.Any(a => a.Equals("--fake-temps", StringComparison.OrdinalIgnoreCase));
        FakeTemps = fakeTemps;
        bool autostart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        DumpCpuSensors = e.Args.Any(a => a.Equals("--dump-cpu", StringComparison.OrdinalIgnoreCase));
        SettingsStore.Log("启动: " + string.Join(' ', e.Args) + " | exe=" + Environment.ProcessPath);

        // 把「到底有多少权限」实测一遍：IsInRole(Administrator) 说是管理员、实际却是
        // 低完整性令牌（写不了 HKCU / %APPDATA% / HKLM）的情况确实存在，开机自启那次就是这样。
        // 后面的 IsElevated 一律以这次实测为准，不再用单独一个布尔值误判。
        var privileges = Core.TokenProbe.Probe();
        foreach (string line in Core.TokenProbe.Describe(privileges)) SettingsStore.Log("权限探测: " + line);

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

        // --task-xml <文件>：把将要写入的计划任务定义导出（排查自启问题用，不需要管理员）
        int xmlIndex = Array.FindIndex(e.Args, a => a.Equals("--task-xml", StringComparison.OrdinalIgnoreCase));
        if (xmlIndex >= 0 && xmlIndex + 1 < e.Args.Length)
        {
            string outPath = e.Args[xmlIndex + 1];
            File.WriteAllText(outPath, AutoStart.ExportTaskXml(), System.Text.Encoding.Unicode);
            SettingsStore.Log("已导出计划任务定义: " + outPath);
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => a.Equals("--autostart-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunAutoStartTest();
            return;
        }

        // 单实例检测：不能用「createdNew」判断。上一个实例被强杀（或调试用例）后，
        // 内核对象可能仍被别人打开着，createdNew 会一直是 false，导致「明明没有实例在跑」
        // 却一直提示"已有实例在运行"。这里改为争抢所有权：WaitOne(0) 成功才算拿到。
        // 另外，更新程序后旧实例可能还开着旧代码（表现为"改了也没变化"），
        // 所以先清掉同名旧实例，日志里也记下它们是谁。
        KillStaleInstances();

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
            SettingsStore.Log("检测到已有实例在运行，本次启动退出。实例: " + DescribeInstances().Replace(Environment.NewLine, " | "));
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        Settings = SettingsStore.Load();
        // 用实测结果，不用 IsInRole：自启那次带着管理员 SID，却是低完整性令牌，
        // 结果驱动装不上、托盘图标写不进 HKCU、配置也存不下。
        IsElevated = privileges.Usable;
        SettingsStore.Log($"启动权限判定: usable={IsElevated} {privileges.FailureReason}");

        // 文件本身的强制完整性标签会压低进程 IL：计划任务给的最高权限令牌也挡不住。
        // 这个信息必须和令牌探测一起看，否则会把「文件标签」误当成「任务没提权」。
        string imageLabel = Core.ImageLabel.Describe();
        SettingsStore.Log($"exe 强制完整性标签={imageLabel} 路径={Environment.ProcessPath}");
        if (imageLabel == "低") SettingsStore.Log("文件标签告警: " + Core.ImageLabel.Conclusion(imageLabel));

        // 如果用户给这个 exe 勾过「属性 → 兼容性 → 以管理员身份运行此程序」，
        // 那么开机自启（注册表 Run / 计划任务）会在登录瞬间被 UAC 拦住而静默失败，
        // 表现就是"开机后程序没起来、托盘也没有图标"。这里把这条标记清掉，
        // 让提权由程序自己控制（runas 一次，或计划任务 /RL HIGHEST）。
        TryClearRunAsAdminLayer();

        // 读取 CPU 温度/频率/电压需要 ring0 驱动，因此令牌不完整时自动以管理员身份重启自身。
        // 这里不再给「开机自启」开豁免：计划任务写着 HighestAvailable 并不代表令牌真的可用
        // （实测出现过「管理员 SID + 低完整性级别」，连 HKCU 都写不了，驱动和托盘全废）。
        // 缺权限时走的就是「用户双击 → UAC 提权」这条已验证有效的路径。
        // 令牌已经是「Full 已提升」却仍是低 IL：Windows 认为它不需要再提权，
        // ShellExecute(runas) 不会弹 UAC，只会用同一个低 IL 令牌再启动一个自己。
        // 再加上 exe 带低完整性标签，重启多少次都还是低 IL —— 这条路径必须跳过，
        // 否则日志里会出现误导性的「提权后令牌仍不完整」。
        bool fakeElevation = privileges.ElevationType == 2
                             && privileges.IntegrityRid < Core.TokenProbe.IntegrityHigh
                             && imageLabel == "低";
        bool relaunched = e.Args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));
        if (!IsElevated && !relaunched && Settings.AskElevation && !NoElevate && !fakeElevation)
        {
            SettingsStore.Log(autostart
                ? $"开机自启进程令牌不完整（{privileges.FailureReason}），改用与手动启动相同的完整管理员令牌重启。"
                : $"当前令牌不完整（{privileges.FailureReason}），请求管理员权限提升。");

            if (TryRelaunchElevated(e.Args))
            {
                Shutdown(0);
                return;
            }

            SettingsStore.Log("管理员权限提升未完成（UAC 被取消或失败），以降级模式运行。");
        }
        else if (!IsElevated && fakeElevation && !relaunched)
        {
            SettingsStore.Log("提权重启已跳过：令牌是「已提升」但 exe 的低完整性标签把 IL 压到了「低」，"
                + "runas 不会再弹 UAC，重启只会得到同样的低 IL 进程。"
                + Core.ImageLabel.Conclusion(imageLabel));
        }
        else if (!IsElevated && relaunched)
        {
            // 已经提权重启过一次还是不行：别再循环弹 UAC，把原因留下来供排障
            SettingsStore.Log($"提权后令牌仍不完整（{privileges.FailureReason}），不再重复提权，本次以降级模式运行。"
                + " 文件标签=" + imageLabel + "；" + Core.ImageLabel.Conclusion(imageLabel));
        }
        else if (!IsElevated)
        {
            SettingsStore.Log($"令牌不完整（{privileges.FailureReason}），设置里关掉了自动提权，以降级模式运行。");
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => SettingsStore.Log("未处理异常: " + args.ExceptionObject);

        // --diag：放在提权重启之后，这样自动生成的诊断报告是"管理员视角"的（能看到 MSR 读数）
        if (e.Args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiagnostics();
            return;
        }

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

        // 面板接近全透明或开了鼠标穿透时，用户会以为"程序没起来 / 图标没了"，
        // 这里给一条气泡说明它就在托盘里。
        if (Settings.ClickThrough || Settings.PanelTransparency > 0.8)
        {
            var tip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            tip.Tick += (_, _) =>
            {
                tip.Stop();
                Tray?.ShowTip("CPU 玻璃监控已在托盘运行", "右键托盘图标可显示设置、退出；双击面板可打开设置。");
            };
            tip.Start();
        }

        // 自启状态与传感器无关，先校准，别被后面的驱动初始化卡住
        ReconcileAutoStart();

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

        // 传感器枚举比较耗时，放到窗口显示之后再做，避免启动白屏。
        // 开机自启时还要先等资源管理器（任务栏）就绪：登录瞬间就启动的话，托盘图标会丢、
        // 驱动这一轮也可能加载失败，正好对应"开机后没图标 + 读不到 CPU 信息"这两个现象。
        if (autostart)
        {
            _startedByAutostart = true;
            SettingsStore.Log("自启启动：先等桌面就绪再开传感器");
            Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                bool ready = BootReady.WaitForShell(120, SettingsStore.Log);
                SettingsStore.Log($"桌面等待结束: ready={ready} 用时={clock.ElapsedMilliseconds}ms shell={BootReady.ShellReady()}");
            }).ContinueWith(_ => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartEngineAndFinish)));
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartEngineAndFinish));
        }
    }

    /// <summary>打开传感器、补齐配置默认值、启动采样与看门狗。</summary>
    private void StartEngineAndFinish()
    {
        if (Engine != null)
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
                    Settings.NetDefaultsApplied = true;
                    SettingsStore.Save(Settings);
                    Shell?.ApplySettings(Settings);
                }
                else if (!Settings.NetDefaultsApplied)
                {
                    // 1.1 新增网速指标：老配置里没有，默认补上（只补一次）
                    bool added = false;
                    foreach (string key in new[] { AggKeys.NetDown, AggKeys.NetUp })
                    {
                        if (Engine.Catalog.ContainsKey(key) && !Settings.SelectedKeys.Contains(key))
                        {
                            Settings.SelectedKeys.Add(key);
                            added = true;
                        }
                    }

                    Settings.NetDefaultsApplied = true;
                    SettingsStore.Save(Settings);
                    if (added) Shell?.ApplySettings(Settings);
                }

                if (!Settings.IconsApplied)
                {
                    // 1.2 用图标代替文字标签，面板可以窄很多：把老配置里偏宽的宽度收一次
                    if (Settings.WindowWidth > 200) Settings.WindowWidth = 180;
                    Settings.IconsApplied = true;
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
            StartDriverWatchdog();

            // 令牌不完整的自启实例不会启动驱动看门狗（没权限重启也没意义），
            // 结果反而是最需要排障的那次没有 boot-diag.txt。这里补写一份。
            if (!IsElevated && _startedByAutostart) WriteBootReport();
        }
    }

    /// <summary>
    /// 自启启动且温度始终读不到时，把现场写进程序目录的 boot-diag.txt。
    /// 开机后用户看不到日志，这份文件是唯一能带出来的证据。
    /// </summary>
    private void WriteBootReport()
    {
        try
        {
            var lines = new List<string>
            {
                "CpuGlassMonitor 开机自启诊断",
                "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "程序: " + Environment.ProcessPath,
                "版本: " + typeof(App).Assembly.GetName().Version,
                "管理员(实测可用): " + IsElevated,
                "令牌诊断: " + Core.TokenProbe.Probe().FailureReason,
                "完整性级别: " + Core.TokenProbe.Probe().IntegrityName,
                "exe 强制完整性标签: " + Core.ImageLabel.Describe(),
                "文件标签结论: " + Core.ImageLabel.Conclusion(Core.ImageLabel.Describe()),
                "建议安装目录: " + Core.ImageLabel.SafeDirectory,
                "计划任务: " + Core.AutoStart.Describe().Replace(Environment.NewLine, " | "),
                "桌面就绪: " + BootReady.ShellReady(),
                "",
                "---- 驱动 ----",
                SensorEngine.DescribeDriverState(),
                "",
                "---- 托盘 ----",
                TrayController.DescribeTrayEntries(),
                ""
            };

            if (Engine != null)
            {
                lines.Add("---- 聚合值 ----");
                lines.Add("cpuTempAvailable=" + Engine.CpuTemperatureAvailable);
                lines.Add("");
                lines.Add("---- CPU 传感器 ----");
                lines.AddRange(Engine.DumpCpuSensors());
                lines.Add("");
                lines.Add("---- MSR / 热区 / EC ----");
                Engine.DumpThermalZone(lines);
            }

            string path = Path.Combine(AppContext.BaseDirectory, "boot-diag.txt");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines), System.Text.Encoding.UTF8);
            SettingsStore.Log("开机自启诊断已写入: " + path);
        }
        catch (Exception ex)
        {
            SettingsStore.Log("开机自启诊断写入失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 已经是管理员却读不到 CPU 温度时，重载一次传感器驱动。
    /// 开机自启瞬间 ring0 驱动没加载成功（或驱动服务指向旧 .sys）时靠这个自愈，
    /// 不用用户手动退出重开。
    /// </summary>
    private void StartDriverWatchdog()
    {
        if (Engine == null || !IsElevated || Engine.FakeTemps)
        {
            SettingsStore.Log($"驱动看门狗未启动: engine={(Engine != null)} elevated={IsElevated} fakeTemps={Engine?.FakeTemps}");
            return;
        }

        _driverWatch?.Stop();
        _driverRestarts = 0;

        // 自启启动时系统刚开机，服务/驱动子系统可能还在初始化，多给几次机会、间隔也放长。
        int maxRestarts = _startedByAutostart ? 10 : 2;
        int intervalSeconds = _startedByAutostart ? 15 : 6;

        SettingsStore.Log($"驱动看门狗已启动（{intervalSeconds} 秒后检查 CPU 温度，最多重载 {maxRestarts} 次，自启={_startedByAutostart}）");
        _driverWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(intervalSeconds) };
        _driverWatch.Tick += (_, _) =>
        {
            if (Engine == null) return;

            if (Engine.CpuTemperatureAvailable)
            {
                _driverWatch?.Stop();
                return;
            }

            // 第一次发现没温度时，把 CPU 传感器全量打一遍，方便事后看日志定位
            // （是传感器没建出来、还是建了读不到、还是被 VBS 拦了）。
            if (!_diagnosed)
            {
                _diagnosed = true;
                try
                {
                    var lines = new List<string> { "---- 温度缺失诊断 ----", SensorEngine.DescribeDriverState() };
                    foreach (string line in Engine.DumpCpuSensors()) lines.Add(line);
                    Engine.DumpThermalZone(lines);
                    foreach (string line in lines) SettingsStore.Log(line);
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("温度诊断失败: " + ex.Message);
                }
            }

            if (_driverRestarts >= maxRestarts)
            {
                _driverWatch?.Stop();
                SettingsStore.Log("CPU 温度始终读不到，已放弃自动重载: " + SensorEngine.DescribeDriverState());

                // 自启场景下用户往往看不到日志：留一份报告在程序目录，下次直接取。
                if (_startedByAutostart) WriteBootReport();
                return;
            }

            _driverRestarts++;
            int attempt = _driverRestarts;
            SettingsStore.Log($"CPU 温度读不到，第 {attempt} 次重载传感器驱动");
            Task.Run(() =>
            {
                string note = "";

                // 第二次重载前先清掉可能指向旧路径的驱动服务
                if (attempt >= 2)
                {
                    bool reset = SensorEngine.TryResetDriverService(out string resetNote);
                    SettingsStore.Log($"重置驱动服务: ok={reset} {resetNote}");
                }

                // 每次都先试着自己注册/启动驱动服务，并把 sc.exe 的原话记下来：
                // 开机自启时到底卡在哪一步（没权限？文件没出来？）只能靠它判断。
                try
                {
                    bool installed = SensorEngine.EnsureDriverServiceInstalled(out string installNote);
                    SettingsStore.Log($"驱动自装: ok={installed} {installNote}");
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("驱动自装异常: " + ex.Message);
                }

                bool ok = Engine != null && Engine.RestartHardware(out note);
                SettingsStore.Log($"重载传感器驱动完成: ok={ok} {note}");
                if (!ok) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Shell?.ApplySettings(Settings);
                    Tray?.SyncState();
                }));
            });
        };
        _driverWatch.Start();
    }

    /// <summary>
    /// 启动后对照一次"开机自启"的真实状态：注册项被手动删掉、或上次设置只写了配置文件没落地时，
    /// 把 settings.json 里的标记纠正过来，避免托盘/设置面板显示成勾选却取消不掉。
    /// </summary>
    private void ReconcileAutoStart()
    {
        Task.Run(() =>
        {
            bool registered;
            try
            {
                registered = AutoStart.IsRegistered;

                // 自启项如果还指着旧 exe 路径（换过发布目录），开机启动的就是旧副本，
                // 现象是"开机后程序没起来"。这里先修路径，再升级权限。
                if (registered)
                {
                    AutoStart.EnsureTargetsCurrentExe(out _);
                    AutoStart.EnsureElevatedAutostart(IsElevated, out _);
                    registered = AutoStart.IsRegistered;
                }
                else if (Settings.WithWindows && IsElevated)
                {
                    // 配置里说「要自启」但系统里没了（被手动删过、或上次只写了配置）：
                    // 现在自己是管理员，直接补注册一次，别把用户的设置默默改回"不开自启"。
                    bool ok = AutoStart.Enable(out string message);
                    SettingsStore.Log($"开机自启：系统里没有但配置要求自启，已重新注册: ok={ok} {message}");
                    registered = AutoStart.IsRegistered;
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("开机自启状态读取失败: " + ex.Message);
                return;
            }

            // schtasks 查询在后台线程做，结果回到 UI 线程再动配置与界面
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (Settings.WithWindows != registered)
                    {
                        Settings.WithWindows = registered;
                        SettingsStore.Save(Settings);
                        SettingsStore.Log($"开机自启：配置与系统状态不一致，已纠正为 {registered}");
                    }

                    Tray?.SyncState();
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("开机自启状态校准失败: " + ex.Message);
                }
            }));
        });
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
                            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--scroll-end", StringComparison.OrdinalIgnoreCase)))
                            {
                                window.ScrollOptionsToEnd();
                                window.UpdateLayout();
                            }

                            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--scroll-list", StringComparison.OrdinalIgnoreCase)))
                            {
                                window.ScrollListToEnd();
                                window.UpdateLayout();
                            }

                            CaptureVisual(window, path, "settings");
                        }                        catch (Exception ex)
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
            IsElevated = Core.TokenProbe.Probe().Usable;
            lines.Add("elevated(令牌可用)=" + IsElevated);
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

            // --dump-cpu：把 CPU 的每个传感器（含 null）都打出来，用来判断"传感器没创建"还是"创建了但读不到"
            if (DumpCpuSensors)
            {
                lines.Add("---- dump cpu sensors ----");
                foreach (string line in Engine.DumpCpuSensors()) lines.Add(line);
                Engine.DumpThermalZone(lines);
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

    /// <summary>
    /// --diag：把"为什么读不到温度 / 为什么没有托盘图标"需要的信息全收集到一个文本文件里，
    /// 写在程序目录（一定能写），并弹框告诉用户文件在哪。用于人工排障。
    /// </summary>
    private void RunDiagnostics()
    {
        var lines = new List<string>();
        string reportPath;
        try
        {
            lines.Add("CpuGlassMonitor 诊断报告");
            lines.Add("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            lines.Add("程序: " + Environment.ProcessPath);
            lines.Add("版本: " + typeof(App).Assembly.GetName().Version);
            lines.Add("系统: " + Environment.OSVersion.VersionString + " (build " + Environment.OSVersion.Version.Build + ")");
            lines.Add("64 位进程: " + Environment.Is64BitProcess);
            var probe = Core.TokenProbe.Probe();
            lines.Add("IsInRole(管理员)=" + CheckElevated() + " 令牌实测可用=" + probe.Usable
                      + " 完整性级别=" + probe.IntegrityName + " 原因=" + probe.FailureReason);
            lines.Add("配置目录: " + SettingsStore.Directory);
            lines.Add("");

            lines.Add("---- 设置 ----");
            Settings = SettingsStore.Load();
            lines.Add($"WithWindows={Settings.WithWindows} AskElevation={Settings.AskElevation} PanelTransparency={Settings.PanelTransparency} Horizontal={Settings.Horizontal} SelectedKeys={Settings.SelectedKeys.Count}");
            lines.Add("");

            lines.Add("---- 开机自启 ----");
            lines.Add("IsRegistered=" + Safe(() => AutoStart.IsRegistered.ToString()));
            lines.Add("任务文件存在=" + Safe(() => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks", "CpuGlassMonitor")).ToString()));
            lines.Add("任务说明=" + Safe(() => AutoStart.Describe()));
            lines.Add("");

            lines.Add("---- 托盘 ----");
            lines.Add(TrayController.DescribeTrayEntries());
            lines.Add("");

            lines.Add("---- 驱动 ----");
            lines.Add(Safe(() => SensorEngine.DescribeDriverState()));
            lines.Add("");

            lines.Add("---- 正在运行的实例 ----");
            lines.Add(DescribeInstances());
            lines.Add("");

            lines.Add("---- 传感器 ----");
            Engine = new SensorEngine { FakeTemps = FakeTemps };
            Engine.Open();
            lines.Add($"open cpu={Engine.CpuName} gpu={Engine.GpuName} catalog={Engine.Catalog.Count} cpuTemp={Engine.CpuTemperatureAvailable} error={Engine.Error ?? "-"}");

            SensorReading? last = null;
            Engine.Updated += r => last = r;
            Engine.IntervalMs = 500;
            Engine.Start();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000 && last == null) Thread.Sleep(100);
            Thread.Sleep(600);

            if (last != null)
            {
                lines.Add("agg.cpu.temp.max = " + Describe(last.Get(AggKeys.CpuTempMax)));
                lines.Add("agg.cpu.clock.avg = " + Describe(last.Get(AggKeys.CpuClockAvg)));
                lines.Add("agg.cpu.voltage = " + Describe(last.Get(AggKeys.CpuVoltage)));
                lines.Add("agg.gpu.temp = " + Describe(last.Get(AggKeys.GpuTemp)));
            }
            else
            {
                lines.Add("reading=NONE");
            }

            lines.Add("");
            lines.Add("---- CPU 传感器明细 ----");
            foreach (string line in Engine.DumpCpuSensors()) lines.Add(line);

            lines.Add("");
            lines.Add("---- MSR / 热区 / EC ----");
            Engine.DumpThermalZone(lines);

            Engine.Dispose();
            Engine = null;
        }
        catch (Exception ex)
        {
            lines.Add("DIAG EXCEPTION: " + ex);
        }

        reportPath = Path.Combine(AppContext.BaseDirectory, "diag-report.txt");
        try
        {
            File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            reportPath = Path.Combine(Path.GetTempPath(), "CpuGlassMonitor-diag.txt");
            try { File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine + "写程序目录失败: " + ex.Message, System.Text.Encoding.UTF8); } catch { }
        }

        foreach (string line in lines) SettingsStore.Log("[diag] " + line);

        // 打开报告并弹框，用户点一下确定就完成，不需要找文件
        try { Process.Start(new ProcessStartInfo("notepad.exe", '"' + reportPath + '"') { UseShellExecute = true }); } catch { }
        MessageBox.Show("诊断完成，报告已保存到：\n" + reportPath + "\n\n（已用记事本打开，直接把这个文件发给开发者即可）",
            "CpuGlassMonitor 诊断", MessageBoxButton.OK, MessageBoxImage.Information);

        Shutdown(0);
    }

    /// <summary>
    /// 清掉同名的旧实例。升级后旧进程还在跑旧代码（面板写着"已是最新"却还是老样子），
    /// 或者旧实例已卡死不给托盘图标，都会让人以为"改了没用"。自己除外。
    /// </summary>
    private static void KillStaleInstances()
    {
        try
        {
            int self = Environment.ProcessId;
            Process[] list = Process.GetProcessesByName("CpuGlassMonitor");
            if (list.Length == 0) return;

            foreach (Process p in list)
            {
                if (p.Id == self) continue;

                string path = "-";
                try { path = p.MainModule?.FileName ?? "-"; } catch { }

                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    SettingsStore.Log($"已结束旧实例: pid={p.Id} start={p.StartTime:HH:mm:ss} path={path}");
                }
                catch (Exception ex)
                {
                    SettingsStore.Log($"结束旧实例失败: pid={p.Id} path={path} {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("清理旧实例时出错: " + ex.Message);
        }
    }

    private static string Describe(double? v) => v.HasValue ? v.Value.ToString("0.##") : "n/a";
    private static string Safe(Func<string> f)
    {
        try { return f() ?? "-"; } catch (Exception ex) { return "ERR: " + ex.Message; }
    }

    private static string DescribeInstances()
    {
        try
        {
            var list = Process.GetProcessesByName("CpuGlassMonitor");
            if (list.Length == 0) return "无";
            var sb = new System.Text.StringBuilder();
            foreach (Process p in list)
            {
                string path = "-";
                try { path = p.MainModule?.FileName ?? "-"; } catch { }
                sb.AppendLine($"{p.Id} start={p.StartTime:yyyy-MM-dd HH:mm:ss} path={path}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return "ERR: " + ex.Message;
        }
    }

    /// <summary>无界面自检：开机自启的开关/取消是否真的落到系统上（用于自动化验证）。</summary>
    private void RunAutoStartTest()
    {
        var lines = new List<string>();
        try
        {
            lines.Add("initial=" + AutoStart.IsRegistered);
            lines.Add("taskExists=" + AutoStart.TaskExists() + " runKey=" + AutoStart.RunKeyExists()
                + " taskElevated=" + AutoStart.TaskRunsElevated());
            lines.Add("ensure elevated -> " + AutoStart.EnsureElevatedAutostart(Core.TokenProbe.Probe().Usable, out string note) + " " + note);

            bool ok1 = AutoStart.Sync(true, out string m1, allowElevationPrompt: false);
            lines.Add($"enable ok={ok1} registered={AutoStart.IsRegistered} msg={m1}");

            bool ok2 = AutoStart.Sync(false, out string m2, allowElevationPrompt: false);
            lines.Add($"disable ok={ok2} registered={AutoStart.IsRegistered} msg={m2}");

            bool ok3 = AutoStart.Sync(false, out string m3, allowElevationPrompt: false);
            lines.Add($"disable-again ok={ok3} registered={AutoStart.IsRegistered} msg={m3}");

            bool ok4 = AutoStart.Sync(true, out string m4, allowElevationPrompt: false);
            lines.Add($"enable-again ok={ok4} registered={AutoStart.IsRegistered} msg={m4}");

            bool ok5 = AutoStart.Sync(false, out string m5, allowElevationPrompt: false);
            lines.Add($"final-disable ok={ok5} registered={AutoStart.IsRegistered} msg={m5}");
        }
        catch (Exception ex)
        {
            lines.Add("AUTOSTART TEST EXCEPTION: " + ex);
        }

        foreach (string line in lines) SettingsStore.Log("[autostart-test] " + line);
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

    /// <summary>
    /// 清掉本程序路径上的「以管理员身份运行」兼容性标记。
    /// 这条标记会让开机自启在登录瞬间被 UAC 拦住而静默失败（用户看到的就是"没启动、没托盘图标"）。
    /// 失败只记日志，不影响启动。
    /// </summary>
    private static void TryClearRunAsAdminLayer()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                writable: true);

            if (key == null) return;

            if (key.GetValue(exe) is not string value) return;
            if (!value.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase)) return;

            key.DeleteValue(exe, throwOnMissingValue: false);
            SettingsStore.Log("已移除本程序路径上的「以管理员身份运行」兼容性标记: " + exe);
        }
        catch (Exception ex)
        {
            SettingsStore.Log("清理兼容性标记失败: " + ex.Message);
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

    /// <summary>
    /// 托盘菜单用：以管理员身份重新拉起自己再退出（会弹一次 UAC）。
    /// 开机自启那次如果拿到的令牌不完整（驱动装不上、温度读不到），
    /// 用户点这一项就能立刻回到"手动启动"的正常状态，不用重启电脑。
    /// </summary>
    public static void RelaunchElevatedNow()
    {
        if (TryRelaunchElevated(Environment.GetCommandLineArgs().Skip(1).ToArray()))
        {
            ExitCurrent();
        }
    }
}
