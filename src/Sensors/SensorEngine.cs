using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace CpuGlassMonitor.Sensors;

public static class AggKeys
{
    public const string CpuTempMax = "agg.cpu.temp.max";
    public const string CpuTempAvg = "agg.cpu.temp.avg";
    public const string CpuClockAvg = "agg.cpu.clock.avg";
    public const string CpuClockMax = "agg.cpu.clock.max";
    public const string CpuLoad = "agg.cpu.load";
    public const string CpuPower = "agg.cpu.power";
    public const string CpuVoltage = "agg.cpu.voltage";

    public const string GpuTemp = "agg.gpu.temp";
    public const string GpuHotspot = "agg.gpu.hotspot";
    public const string GpuLoad = "agg.gpu.load";
    public const string GpuPower = "agg.gpu.power";
    public const string GpuClock = "agg.gpu.clock";
    public const string GpuVram = "agg.gpu.vram";

    public const string RamLoad = "agg.ram.load";
    public const string RamUsed = "agg.ram.used";
    public const string RamFree = "agg.ram.free";

    public const string NetDown = "agg.net.down";
    public const string NetUp = "agg.net.up";
}

/// <summary>一次采样的结果。</summary>
public sealed class SensorReading
{
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    public DateTime Timestamp { get; } = DateTime.Now;

    public double? Get(string key) => Values.TryGetValue(key, out double v) ? v : null;
}

/// <summary>
/// 后台传感器采集引擎：封装 LibreHardwareMonitor，负责读取原始传感器 + 计算聚合指标。
/// 采集在独立线程中进行，UI 只接收快照。
/// </summary>
public sealed class SensorEngine : IDisposable
{
    private readonly List<IHardware> _hardware = new();
    private readonly Dictionary<string, (IHardware Hardware, ISensor Sensor)> _rawSensors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetricSpec> _catalog = new(StringComparer.Ordinal);
    private readonly List<(string Key, Func<SensorReading, double?> Compute)> _aggregates = new();
    private readonly NetworkSampler _network = new();
    private readonly ThermalZone _thermalZone = new();
    private readonly LenovoEcTemperature _ecTemperature = new();
    private bool _thermalZoneLogged;
    private bool _ecTemperatureLogged;
    private bool _dtsLogged;

    private Computer? _computer;
    private Thread? _worker;
    private volatile bool _running;
    private volatile int _intervalMs = 1000;

    // 采集线程与「重载传感器」互斥，避免 Close/Open 和 Update 撞在一起
    private readonly object _sync = new();

    public IReadOnlyDictionary<string, MetricSpec> Catalog => _catalog;

    public string CpuName { get; private set; } = "CPU";

    public string GpuName { get; private set; } = "";

    public string? Error { get; private set; }

    /// <summary>是否读到了 CPU 温度（需要管理员权限）。</summary>
    public bool CpuTemperatureAvailable { get; private set; }

    /// <summary>调试开关（--fake-temps）：往读数里塞假温度/频率，非管理员环境也能验证界面。</summary>
    public bool FakeTemps { get; set; }

    public bool IsOpen { get; private set; }

    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, 200, 10000);
    }

    public event Action<SensorReading>? Updated;

    public void Open()
    {
        // 换过 exe 目录（比如从 src\bin 换到 dist）之后，旧的服务还指着已经删掉的 .sys，
        // LHM 看到"服务已存在"就跳过安装、直接启动，于是永远读不到 MSR。
        // 这里在 Open 之前先把这个过期服务清掉，让 LHM 用当前目录重新装一份。
        DropStaleDriverService();

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false
            };
            _computer.Open();
            IsOpen = true;
        }
        catch (Exception ex)
        {
            Error = "传感器初始化失败：" + ex.Message;
            SettingsStoreShim.Log("Computer.Open 失败: " + ex);
            return;
        }

        CollectHardware(_computer);
        SensorReading first = ReadOnce();
        BuildCatalog(first);
        SettingsStoreShim.Log("传感器: " + DescribeDriverState());
    }

    private void CollectHardware(Computer computer)
    {
        foreach (IHardware hw in computer.Hardware)
        {
            _hardware.Add(hw);
            try
            {
                hw.Update();
            }
            catch (Exception ex)
            {
                SettingsStoreShim.Log($"更新硬件 {hw.Name} 失败: {ex.Message}");
            }

            foreach (IHardware sub in hw.SubHardware)
            {
                _hardware.Add(sub);
                try
                {
                    sub.Update();
                }
                catch (Exception ex)
                {
                    SettingsStoreShim.Log($"更新子硬件 {sub.Name} 失败: {ex.Message}");
                }
            }
        }
    }

    public void Start()
    {
        if (_worker != null) return;
        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "CpuGlassMonitor.Sensors",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                SensorReading reading;
                lock (_sync)
                {
                    reading = ReadOnce();
                }

                Updated?.Invoke(reading);
            }
            catch (Exception ex)
            {
                SettingsStoreShim.Log("采集失败: " + ex);
            }

            int remain = _intervalMs - (int)sw.ElapsedMilliseconds;
            if (remain < 30) remain = 30;
            for (int slept = 0; slept < remain && _running; slept += 30)
            {
                Thread.Sleep(Math.Min(30, remain - slept));
            }
        }
    }

    /// <summary>
    /// 重载传感器（关掉 LHM 再开一次）。
    /// 用于「已经是管理员，但 CPU 温度/频率/电压全是空」的情况：常见原因是
    /// ring0 驱动在开机瞬间没加载成功，或者驱动服务指向了旧的 .sys 文件。
    /// 返回是否重载成功，note 写日志给用户看。
    /// </summary>
    public bool RestartHardware(out string note)
    {
        note = "";
        try
        {
            lock (_sync)
            {
                try
                {
                    _computer?.Close();
                }
                catch
                {
                    // 忽略
                }

                _computer = null;
                _hardware.Clear();
                _rawSensors.Clear();

                Open();
            }

            note = DescribeDriverState();
            return IsOpen;
        }
        catch (Exception ex)
        {
            note = "重载失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 强制重装 ring0 驱动服务：常见故障是服务注册在旧路径（换过 exe 目录、或另一边副本装过），
    /// 新副本就再也读不到 CPU 温度。这里把服务删掉，让 LHM 下次 Open 重新装一份。
    /// 需要管理员权限，失败就安静返回 false。
    /// </summary>
    public static bool TryResetDriverService(out string note)
    {
        note = "";
        try
        {
            string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "CpuGlassMonitor");
            string serviceName = "R0" + exeName;
            string sysPath = Path.Combine(AppContext.BaseDirectory, exeName + ".sys");

            var psi = new ProcessStartInfo("sc.exe", $"delete {serviceName}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                note = "无法启动 sc.exe";
                return false;
            }

            p.WaitForExit(8000);

            try
            {
                if (File.Exists(sysPath)) File.Delete(sysPath);
            }
            catch
            {
                // 文件被占用就留着，下次 Open 会覆盖
            }

            note = $"已删除驱动服务 {serviceName}（exit={p.ExitCode}）";
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            note = "重置驱动服务失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 如果驱动服务已经注册，但它的 ImagePath 指向的 .sys 已经不存在（换过 exe 目录的典型症状），
    /// 就把服务停掉删掉，让 LHM 的 Open 用当前目录重新装一份。需要管理员权限，失败只记日志。
    /// </summary>
    private static void DropStaleDriverService()
    {
        try
        {
            string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "CpuGlassMonitor");
            string serviceName = "R0" + exeName;
            string expected = Path.Combine(AppContext.BaseDirectory, exeName + ".sys");

            string? imagePath = null;
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
                imagePath = key?.GetValue("ImagePath") as string;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(imagePath)) return;

            string file = imagePath.Replace(@"\??\", "").Trim();

            // 只要服务指向的就是「本程序目录下的同名 .sys」就不动它。
            // 注意不能额外要求 File.Exists：首次运行时 LHM 还没把 .sys 释放出来，
            // 那样会误判成"旧路径"，每次启动都白删一遍服务。
            bool sameAsOurs;
            try
            {
                sameAsOurs = string.Equals(
                    Path.GetFullPath(file),
                    Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                sameAsOurs = string.Equals(file, expected, StringComparison.OrdinalIgnoreCase);
            }

            // 服务指向本程序目录下的同名 .sys，且那个文件真的在 → 状态正常，不动它。
            // 如果文件不在（换过目录、杀软删过、发布时被覆盖掉），服务就永远起不来，
            // LibreHardwareMonitor 又会因为"服务已存在"而跳过安装，于是彻底没有 MSR
            // （温度、频率、电压全空）。这种情况必须把服务删掉，让它重新释放驱动。
            bool fileExists;
            try
            {
                fileExists = File.Exists(file);
            }
            catch
            {
                fileExists = false;
            }

            if (sameAsOurs && fileExists) return;

            SettingsStoreShim.Log($"驱动服务不可用，准备重装：image={imagePath} 期望={expected} 文件存在={fileExists}");
            RunSc($"stop {serviceName}");
            RunSc($"delete {serviceName}");
        }
        catch (Exception ex)
        {
            SettingsStoreShim.Log("检查驱动服务失败: " + ex.Message);
        }
    }

    /// <summary>跑一条 sc 命令，吞掉所有异常，只返回退出码。</summary>
    private static int RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? p = Process.Start(psi);
            if (p == null) return -1;

            p.WaitForExit(8000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>把 ring0 驱动服务/文件的状态写下来，方便判断"为什么读不到温度"。</summary>
    public static string DescribeDriverState()
    {
        try
        {
            string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "CpuGlassMonitor");
            string serviceName = "R0" + exeName;
            string sysPath = Path.Combine(AppContext.BaseDirectory, exeName + ".sys");

            string imagePath = "";
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
                imagePath = key?.GetValue("ImagePath") as string ?? "";
            }
            catch
            {
                // 忽略
            }

            return $"驱动服务={serviceName} image={imagePath} sys存在={File.Exists(sysPath)} 进程={Environment.ProcessPath}";
        }
        catch (Exception ex)
        {
            return "驱动状态读取失败: " + ex.Message;
        }
    }

    private SensorReading ReadOnce()
    {
        var reading = new SensorReading();
        foreach (IHardware hw in _hardware)
        {
            try
            {
                hw.Update();
            }
            catch
            {
                // 单个硬件读取失败不影响其它硬件
            }

            foreach (ISensor sensor in hw.Sensors)
            {
                double? value = sensor.Value;
                if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) continue;
                reading.Values[sensor.Identifier.ToString()] = value.Value;
            }
        }

        foreach ((string key, Func<SensorReading, double?> compute) in _aggregates)
        {
            double? v = null;
            try
            {
                v = compute(reading);
            }
            catch
            {
                // 聚合失败忽略
            }

            if (v.HasValue && !double.IsNaN(v.Value)) reading.Values[key] = v.Value;
        }

        // 网速：由程序自己按累计字节差值算，和 LHM 无关（LHM 在 Windows 上没有网络吞吐）
        _network.Sample();
        reading.Values[AggKeys.NetDown] = _network.DownBytesPerSecond;
        reading.Values[AggKeys.NetUp] = _network.UpBytesPerSecond;

        // CPU 温度兜底 1：自己读 Intel DTS（MSR 0x19C）。
        // LHM 的 Intel 温度要先读 MSR 0x1A2 拿 TjMax，那一步在开了 VBS 的机器上常失败，
        // 于是所有核心温度都是 null；这里绕开它，TjMax 读不到就按 100°C 算。
        if (!FakeTemps && !reading.Get(AggKeys.CpuTempMax).HasValue &&
            IntelDtsTemperature.TryRead(out double dtsMax, out double dtsAvg))
        {
            reading.Values[AggKeys.CpuTempMax] = dtsMax;
            reading.Values[AggKeys.CpuTempAvg] = dtsAvg;
            if (!_dtsLogged)
            {
                _dtsLogged = true;
                SettingsStoreShim.Log($"CPU 温度来源=自读 MSR 0x19C（{IntelDtsTemperature.LastStatus}）");
            }
        }

        // CPU 温度兜底 2：ACPI 热区（粗一点，很多笔记本根本不支持）。
        if (!FakeTemps && !reading.Get(AggKeys.CpuTempMax).HasValue)
        {
            double? zone = _thermalZone.Read();
            if (zone.HasValue)
            {
                reading.Values[AggKeys.CpuTempMax] = zone.Value;
                reading.Values[AggKeys.CpuTempAvg] = zone.Value;
                if (!_thermalZoneLogged)
                {
                    _thermalZoneLogged = true;
                    SettingsStoreShim.Log("CPU 温度来源=ACPI 热区（LHM 读不到 MSR，可能是内核隔离/VBS 拦住）");
                }
            }
        }

        // CPU 温度兜底 2：联想 EC（root\WMI:LENOVO_GAMEZONE_DATA.GetCPUTemp）。
        // 开了 VBS / 内存完整性后 Intel DTS 的 MSR 会被拦，LHM 拿不到温度，
        // 这时走联想电脑管家用的那条 EC 接口仍然是准的。
        if (!FakeTemps && !reading.Get(AggKeys.CpuTempMax).HasValue)
        {
            double? ec = _ecTemperature.Read();
            if (ec.HasValue)
            {
                reading.Values[AggKeys.CpuTempMax] = ec.Value;
                reading.Values[AggKeys.CpuTempAvg] = ec.Value;
                if (!_ecTemperatureLogged)
                {
                    _ecTemperatureLogged = true;
                    SettingsStoreShim.Log("CPU 温度来源=联想 EC（LHM 读不到 MSR)");
                }
            }
        }

        // 温度不是一上电就能读到：ring0 驱动装好后第一次 Update 往往还是 null。
        // 所以这个标记必须是「只要读到过一次就为真」，否则 UI 会一直停在"需要管理员权限"。
        if (!CpuTemperatureAvailable &&
            (reading.Get(AggKeys.CpuTempMax).HasValue || reading.Get(AggKeys.CpuTempAvg).HasValue))
        {
            CpuTemperatureAvailable = true;
        }

        // 调试用（--fake-temps）：非管理员环境下也要能验证 UI 有温度时的样子
        if (FakeTemps)
        {
            reading.Values[AggKeys.CpuTempMax] = 66;
            reading.Values[AggKeys.CpuTempAvg] = 61;
            reading.Values[AggKeys.CpuClockAvg] = 4200;
            reading.Values[AggKeys.CpuClockMax] = 5100;
            reading.Values[AggKeys.CpuVoltage] = 1.28;
            CpuTemperatureAvailable = true;
        }

        return reading;
    }

    // ---------------------------------------------------------------- catalog

    private void BuildCatalog(SensorReading first)
    {
        _rawSensors.Clear();

        // 1) 原始传感器
        foreach (IHardware hw in _hardware)
        {
            string group = GroupName(hw.HardwareType);
            foreach (ISensor sensor in hw.Sensors)
            {
                string key = sensor.Identifier.ToString();
                _rawSensors[key] = (hw, sensor);

                (string unit, double scale, int digits, double? warn, double? crit) = FormatOf(sensor.SensorType);
                bool advanced = IsAdvanced(sensor);

                // CPU/GPU 的温度、频率、负载等原始传感器与聚合指标重复，默认收进"杂项"，
                // 只把聚合项（CPU 温度 / CPU 频率 / GPU 热点 …）作为常规选项暴露出来。
                bool curated = hw.HardwareType == HardwareType.Cpu
                               || hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
                if (curated && sensor.SensorType is SensorType.Temperature or SensorType.Clock
                    or SensorType.Load or SensorType.Power or SensorType.Voltage)
                {
                    advanced = true;
                }
                _catalog[key] = new MetricSpec
                {
                    Key = key,
                    Group = group,
                    SubGroup = TypeName(sensor.SensorType),
                    Label = sensor.Name,
                    Unit = unit,
                    Scale = scale,
                    Digits = digits,
                    Warn = warn,
                    Crit = crit,
                    Advanced = advanced
                };
            }
        }

        // 2) 名称
        IHardware? cpu = _hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null) CpuName = cpu.Name;

        IHardware? gpu = _hardware.FirstOrDefault(h =>
            h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
        if (gpu != null) GpuName = gpu.Name;

        // 3) 聚合指标
        _aggregates.Clear();
        BuildCpuAggregates(cpu);
        BuildGpuAggregates(gpu);
        BuildRamAggregates();
        BuildNetworkAggregates();

        CpuTemperatureAvailable =
            first.Get(AggKeys.CpuTempMax).HasValue || first.Get(AggKeys.CpuTempAvg).HasValue;

        if (_catalog.Count == 0)
        {
            Error ??= "未发现任何传感器（可能缺少驱动或权限）。";
        }
    }

    private void AddAggregate(MetricSpec spec, Func<SensorReading, double?> compute)
    {
        _catalog[spec.Key] = spec;
        _aggregates.Add((spec.Key, compute));
    }

    private void BuildCpuAggregates(IHardware? cpu)
    {
        if (cpu == null) return;

        List<(string Key, ISensor Sensor)> temps = Find(cpu, SensorType.Temperature);
        List<(string Key, ISensor Sensor)> clocks = Find(cpu, SensorType.Clock);
        List<(string Key, ISensor Sensor)> loads = Find(cpu, SensorType.Load);
        List<(string Key, ISensor Sensor)> powers = Find(cpu, SensorType.Power);
        List<(string Key, ISensor Sensor)> volts = Find(cpu, SensorType.Voltage);

        // 温度：优先 "Core Max"，其次 "CPU Package"，最后核心温度最大值
        (string Key, ISensor Sensor)? tempMax = temps.FirstOrDefault(t => t.Sensor.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase));
        if (tempMax is null || tempMax.Value.Key is null)
        {
            tempMax = temps.FirstOrDefault(t => t.Sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        }
        if (tempMax is null || tempMax.Value.Key is null)
        {
            tempMax = temps.FirstOrDefault(t => t.Sensor.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase));
        }

        if (tempMax is { Key: not null })
        {
            string key = tempMax.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuTempMax,
                Group = "CPU",
                SubGroup = "温度",
                Label = "CPU 温度",
                Unit = "°C",
                Digits = 0,
                Warn = 80,
                Crit = 92,
                DefaultOn = true,
                SparkMin = 30,
                SparkMax = 100
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? tempAvg = temps.FirstOrDefault(t => t.Sensor.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase));
        if (tempAvg is { Key: not null })
        {
            string key = tempAvg.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuTempAvg,
                Group = "CPU",
                SubGroup = "温度",
                Label = "CPU 平均温度",
                Unit = "°C",
                Digits = 0,
                Warn = 80,
                Crit = 92,
                SparkMin = 30,
                SparkMax = 100
            }, r => r.Get(key));
        }
        else
        {
            List<(string Key, ISensor Sensor)> cores = temps
                .Where(t => t.Sensor.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (cores.Count > 0)
            {
                AddAggregate(new MetricSpec
                {
                    Key = AggKeys.CpuTempAvg,
                    Group = "CPU",
                    SubGroup = "温度",
                    Label = "CPU 平均温度",
                    Unit = "°C",
                    Digits = 0,
                    Warn = 80,
                    Crit = 92,
                    SparkMin = 30,
                    SparkMax = 100
                }, r =>
                {
                    List<double> vals = cores.Select(c => r.Get(c.Key)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                    return vals.Count > 0 ? vals.Average() : null;
                });
            }
        }

        if (clocks.Count > 0)
        {
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuClockAvg,
                Group = "CPU",
                SubGroup = "频率",
                Label = "CPU 频率",
                Unit = "GHz",
                Scale = 0.001,
                Digits = 2,
                DefaultOn = true
            }, r =>
            {
                List<double> vals = clocks.Select(c => r.Get(c.Key)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                return vals.Count > 0 ? vals.Average() : null;
            });

            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuClockMax,
                Group = "CPU",
                SubGroup = "频率",
                Label = "CPU 最高频率",
                Unit = "GHz",
                Scale = 0.001,
                Digits = 2
            }, r =>
            {
                List<double> vals = clocks.Select(c => r.Get(c.Key)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                return vals.Count > 0 ? vals.Max() : null;
            });
        }

        (string Key, ISensor Sensor)? total = loads.FirstOrDefault(l => l.Sensor.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase));
        if (total is { Key: not null })
        {
            string key = total.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuLoad,
                Group = "CPU",
                SubGroup = "负载",
                Label = "CPU 负载",
                Unit = "%",
                Digits = 0,
                Warn = 90,
                Crit = 98,
                DefaultOn = true,
                SparkMin = 0,
                SparkMax = 100
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? power = Pick(powers, p => p.Sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        if (power is { Key: not null })
        {
            string key = power.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuPower,
                Group = "CPU",
                SubGroup = "功耗",
                Label = "CPU 功耗",
                Unit = "W",
                Digits = 1,
                DefaultOn = true
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? volt = Pick(volts, v => v.Sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        if (volt is { Key: not null })
        {
            string key = volt.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.CpuVoltage,
                Group = "CPU",
                SubGroup = "电压",
                Label = "CPU 电压",
                Unit = "V",
                Digits = 3,
                DefaultOn = true,
                SparkMin = 0.5,
                SparkMax = 1.6
            }, r => r.Get(key));
        }
    }

    private void BuildGpuAggregates(IHardware? gpu)
    {
        if (gpu == null) return;

        List<(string Key, ISensor Sensor)> temps = Find(gpu, SensorType.Temperature);
        List<(string Key, ISensor Sensor)> loads = Find(gpu, SensorType.Load);
        List<(string Key, ISensor Sensor)> clocks = Find(gpu, SensorType.Clock);
        List<(string Key, ISensor Sensor)> powers = Find(gpu, SensorType.Power);
        List<(string Key, ISensor Sensor)> small = Find(gpu, SensorType.SmallData);

        (string Key, ISensor Sensor)? core = temps.FirstOrDefault(t => t.Sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        if (core is { Key: not null })
        {
            string key = core.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.GpuTemp,
                Group = "显卡",
                SubGroup = "温度",
                Label = "GPU 温度",
                Unit = "°C",
                Digits = 0,
                Warn = 80,
                Crit = 88,
                SparkMin = 30,
                SparkMax = 100
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? hot = temps.FirstOrDefault(t => t.Sensor.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase));
        if (hot is { Key: not null })
        {
            string key = hot.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.GpuHotspot,
                Group = "显卡",
                SubGroup = "温度",
                Label = "GPU 热点",
                Unit = "°C",
                Digits = 0,
                Warn = 90,
                Crit = 100,
                SparkMin = 30,
                SparkMax = 110
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? load = Pick(loads, l => l.Sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));
        if (load is { Key: not null })
        {
            string key = load.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.GpuLoad,
                Group = "显卡",
                SubGroup = "负载",
                Label = "GPU 负载",
                Unit = "%",
                Digits = 0,
                Warn = 95,
                Crit = 100,
                SparkMin = 0,
                SparkMax = 100
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? gpuClock = Pick(clocks, c => c.Sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));
        if (gpuClock is { Key: not null })
        {
            string key = gpuClock.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.GpuClock,
                Group = "显卡",
                SubGroup = "频率",
                Label = "GPU 频率",
                Unit = "GHz",
                Scale = 0.001,
                Digits = 2
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? gpuPower = powers.FirstOrDefault();
        if (gpuPower is { Key: not null })
        {
            string key = gpuPower.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.GpuPower,
                Group = "显卡",
                SubGroup = "功耗",
                Label = "GPU 功耗",
                Unit = "W",
                Digits = 1
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? vram = small.FirstOrDefault(s => s.Sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase));
        if (vram is { Key: not null })
        {
            string key = vram.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.GpuVram,
                Group = "显卡",
                SubGroup = "显存",
                Label = "显存占用",
                Unit = "GB",
                Digits = 1
            }, r =>
            {
                double? mb = r.Get(key);
                return mb.HasValue ? mb.Value / 1024.0 : null;
            });
        }
    }

    private void BuildRamAggregates()
    {
        IHardware? mem = _hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        if (mem == null) return;

        List<(string Key, ISensor Sensor)> loads = Find(mem, SensorType.Load);
        List<(string Key, ISensor Sensor)> data = Find(mem, SensorType.Data);

        (string Key, ISensor Sensor)? load = loads.FirstOrDefault(l => l.Sensor.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase));
        if (load is { Key: not null })
        {
            string key = load.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.RamLoad,
                Group = "内存",
                SubGroup = "占用",
                Label = "内存占用",
                Unit = "%",
                Digits = 0,
                Warn = 90,
                Crit = 97,
                SparkMin = 0,
                SparkMax = 100
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? used = data.FirstOrDefault(d => d.Sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase));
        if (used is { Key: not null })
        {
            string key = used.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.RamUsed,
                Group = "内存",
                SubGroup = "占用",
                Label = "内存已用",
                Unit = "GB",
                Digits = 1
            }, r => r.Get(key));
        }

        (string Key, ISensor Sensor)? avail = data.FirstOrDefault(d => d.Sensor.Name.Contains("Memory Available", StringComparison.OrdinalIgnoreCase));
        if (avail is { Key: not null })
        {
            string key = avail.Value.Key;
            AddAggregate(new MetricSpec
            {
                Key = AggKeys.RamFree,
                Group = "内存",
                SubGroup = "占用",
                Label = "内存可用",
                Unit = "GB",
                Digits = 1
            }, r => r.Get(key));
        }
    }

    /// <summary>网络上下行速度：值以「字节/秒」存，显示时换算成 MB/s。</summary>
    private void BuildNetworkAggregates()
    {
        const double BytesToMegabytes = 1.0 / (1024 * 1024);

        AddAggregate(new MetricSpec
        {
            Key = AggKeys.NetDown,
            Group = "网络",
            SubGroup = "速度",
            Label = "下载速度",
            Unit = "MB/s",
            Scale = BytesToMegabytes,
            Digits = 2,
            DefaultOn = true,
            SparkMin = 0
        }, r => r.Get(AggKeys.NetDown));

        AddAggregate(new MetricSpec
        {
            Key = AggKeys.NetUp,
            Group = "网络",
            SubGroup = "速度",
            Label = "上传速度",
            Unit = "MB/s",
            Scale = BytesToMegabytes,
            Digits = 2,
            DefaultOn = true,
            SparkMin = 0
        }, r => r.Get(AggKeys.NetUp));
    }

    private List<(string Key, ISensor Sensor)> Find(IHardware hw, SensorType type)    {
        var list = new List<(string Key, ISensor Sensor)>();
        foreach (ISensor sensor in hw.Sensors)
        {
            if (sensor.SensorType != type) continue;
            list.Add((sensor.Identifier.ToString(), sensor));
        }

        return list;
    }

    /// <summary>按优先条件挑选一个传感器，挑不到时退回列表第一项。</summary>
    private static (string Key, ISensor Sensor)? Pick(
        List<(string Key, ISensor Sensor)> list,
        Func<(string Key, ISensor Sensor), bool> predicate)
    {
        (string Key, ISensor Sensor) hit = list.FirstOrDefault(predicate);
        if (hit.Key is not null) return hit;

        (string Key, ISensor Sensor) fallback = list.FirstOrDefault();
        return fallback.Key is null ? null : fallback;
    }

    // ---------------------------------------------------------------- mapping

    private static string GroupName(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "显卡",
        HardwareType.Memory => "内存",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "主板",
        HardwareType.Storage => "硬盘",
        HardwareType.Network => "网络",
        HardwareType.Cooler => "散热",
        HardwareType.Psu => "电源",
        HardwareType.Battery => "电池",
        _ => "其他"
    };

    private static string TypeName(SensorType type) => type switch
    {
        SensorType.Temperature => "温度",
        SensorType.Clock => "频率",
        SensorType.Voltage => "电压",
        SensorType.Power => "功耗",
        SensorType.Load => "负载",
        SensorType.Fan => "风扇",
        SensorType.Control => "控制",
        SensorType.Data => "容量",
        SensorType.SmallData => "容量",
        SensorType.Energy => "能耗",
        SensorType.Level => "占用",
        SensorType.Humidity => "湿度",
        SensorType.Noise => "噪声",
        SensorType.Flow => "流量",
        SensorType.TimeSpan => "时间",
        SensorType.Throughput => "带宽",
        SensorType.Factor => "系数",
        SensorType.Conductivity => "电导率",
        _ => "其他"
    };

    private static (string Unit, double Scale, int Digits, double? Warn, double? Crit) FormatOf(SensorType type) => type switch
    {
        SensorType.Temperature => ("°C", 1, 0, 80, 92),
        SensorType.Clock => ("GHz", 0.001, 2, null, null),
        SensorType.Voltage => ("V", 1, 3, null, null),
        SensorType.Power => ("W", 1, 1, null, null),
        SensorType.Load => ("%", 1, 0, 90, 99),
        SensorType.Fan => ("RPM", 1, 0, null, null),
        SensorType.Control => ("%", 1, 0, null, null),
        SensorType.Level => ("%", 1, 0, 90, 99),
        SensorType.Data => ("GB", 1, 1, null, null),
        SensorType.SmallData => ("", 1, 0, null, null),
        SensorType.Energy => ("mWh", 1, 0, null, null),
        SensorType.Humidity => ("%", 1, 0, null, null),
        SensorType.Noise => ("dBA", 1, 1, null, null),
        SensorType.Flow => ("L/h", 1, 1, null, null),
        SensorType.TimeSpan => ("s", 1, 1, null, null),
        SensorType.Throughput => ("B/s", 1, 0, null, null),
        SensorType.Factor => ("", 1, 2, null, null),
        SensorType.Conductivity => ("µS/cm", 1, 0, null, null),
        _ => ("", 1, 1, null, null)
    };

    /// <summary>诊断用：把 CPU 硬件的每个传感器（包括取不到值的）逐行描述出来。</summary>
    public List<string> DumpCpuSensors()
    {
        var lines = new List<string>();
        foreach (IHardware hw in _hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu) continue;
            lines.Add("hw=" + hw.Name + " type=" + hw.HardwareType);
            try
            {
                hw.Update();
            }
            catch (Exception ex)
            {
                lines.Add("update failed: " + ex.Message);
            }

            foreach (ISensor sensor in hw.Sensors)
            {
                double? v = sensor.Value;
                string text = v.HasValue ? v.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "null";
                lines.Add($"  {sensor.SensorType}|{sensor.Name}|{sensor.Identifier} = {text}");
            }
        }

        return lines;
    }

    /// <summary>诊断用：把 ACPI 热区的查询结果写进行。</summary>
    public void DumpThermalZone(List<string> lines)
    {
        lines.Add(_thermalZone.Describe());
        lines.Add("LenovoEC: " + _ecTemperature.Status);
        IntelDtsTemperature.Describe(lines);
    }

    private static bool IsAdvanced(ISensor sensor)
    {
        string name = sensor.Name;
        if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Thread #", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase)) return true;
        if (sensor.SensorType is SensorType.SmallData or SensorType.Throughput or SensorType.Factor
            or SensorType.Energy or SensorType.TimeSpan or SensorType.Level) return true;
        if (sensor.SensorType == SensorType.Data) return true;
        return false;
    }

    public void Dispose()
    {        _running = false;
        try
        {
            _worker?.Join(500);
        }
        catch
        {
            // 忽略
        }

        _worker = null;

        try
        {
            _computer?.Close();
        }
        catch
        {
            // 忽略关闭错误
        }

        _computer = null;
        IsOpen = false;
    }
}

/// <summary>避免 Sensors 层直接依赖 Core 命名空间。</summary>
internal static class SettingsStoreShim
{
    public static void Log(string message) => Core.SettingsStore.Log(message);
}
