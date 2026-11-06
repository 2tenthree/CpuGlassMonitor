using System.Diagnostics;
using System.Management;
using CpuGlassMonitor.Core;

namespace CpuGlassMonitor.Sensors;

/// <summary>
/// 联想（Legion / LOQ / 拯救者）专用的 CPU 温度兜底来源。
///
/// 部分机器开启 VBS / 内存完整性后，Intel DTS（MSR 0x19C）会被 hypervisor 拦掉，
/// LibreHardwareMonitor 于是拿不到任何 CPU 温度（但频率/电压还能读，因为走的是别的 MSR）。
/// 这类机器的 ACPI 热区又往往是"不支持"，所以再加一条：直接问联想的 EC 驱动
/// （root\WMI:LENOVO_GAMEZONE_DATA.GetCPUTemp），也就是联想电脑管家 / Vantage 用的那条路。
///
/// WMI 调用有可能把线程卡住（EC 忙、驱动没响应），因此所有查询都放到独立后台线程里，
/// 前台只读它最近一次成功的结果；卡住也只会让这个线程停在那，不会拖慢采样线程。
/// </summary>
internal sealed class LenovoEcTemperature
{
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(6);
    private const string Namespace = @"root\WMI";
    private const string ClassName = "LENOVO_GAMEZONE_DATA";
    private const string MethodName = "GetCPUTemp";

    private readonly object _gate = new();
    private double _value;
    private long _stampTicks;
    private volatile bool _started;
    private volatile bool _unavailable;
    private string _status = "未尝试";

    /// <summary>最近一次查询失败的原因，写进日志便于判断是"没权限"还是"这机型没这个接口"。</summary>
    public static string? LastError { get; private set; }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>返回最近一次 EC 温度；超过 6 秒没更新或不可用时返回 null。</summary>
    public double? Read()
    {
        if (!_started)
        {
            _started = true;
            try
            {
                var thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "LenovoEcTemp",
                    Priority = ThreadPriority.BelowNormal
                };
                thread.Start();
            }
            catch
            {
                _unavailable = true;
            }
        }

        lock (_gate)
        {
            if (_unavailable || _stampTicks == 0) return null;
            if (DateTime.UtcNow.Ticks - _stampTicks > Stale.Ticks) return null;
            return _value;
        }
    }

    private void Loop()
    {
        int failures = 0;

        while (true)
        {
            try
            {
                if (_unavailable) return;

                double? t = Query();
                if (t.HasValue)
                {
                    bool first = false;
                    lock (_gate)
                    {
                        first = _stampTicks == 0;
                        _value = t.Value;
                        _stampTicks = DateTime.UtcNow.Ticks;
                        _status = $"Lenovo EC {t.Value:F0}°C";
                    }

                    if (first) SettingsStore.Log($"LenovoEC 已可用: {t.Value:F0}°C（不依赖 ring0 驱动）");
                    failures = 0;
                }
                else
                {
                    failures++;
                    if (failures == 3)
                    {
                        SettingsStore.Log($"LenovoEC 读不到值（{LastError ?? "root\\WMI:LENOVO_GAMEZONE_DATA 无返回"}），继续重试");
                    }
                    if (failures >= 12)
                    {
                        lock (_gate)
                        {
                            _status = "Lenovo EC 不可用（本机 WMI 接口不提供温度）";
                        }

                        SettingsStore.Log("LenovoEC 放弃：连续 12 次失败");
                        _unavailable = true;
                        return;
                    }
                }

                Thread.Sleep(1000);
            }
            catch
            {
                // 线程本身不能死，吞掉异常继续
                Thread.Sleep(2000);
            }
        }
    }

    private static double? Query()
    {
        // 关键点：LENOVO_GAMEZONE_DATA 的方法必须**在实例上**调用。
        // 早期版本用 ManagementClass.InvokeMethod（类级调用）会一直拿不到值，
        // 于是这条最有可能救场的兜底在多数机器上其实是哑的。
        double? viaInstance = InvokeOnInstance();
        if (viaInstance.HasValue) return viaInstance;

        return InvokeOnClass();
    }

    /// <summary>在实例上调用 GetCPUTemp（联想电脑管家 / Vantage / Legion Toolkit 的做法）。</summary>
    private static double? InvokeOnInstance()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM {ClassName}");
            foreach (ManagementBaseObject raw in searcher.Get())
            {
                using ManagementObject? instance = raw as ManagementObject;
                if (instance is null) continue;

                double? fromMethod = null;
                try
                {
                    using ManagementBaseObject? outParams = instance.InvokeMethod(MethodName, null, null);
                    fromMethod = PickNumber(outParams);
                }
                catch (ManagementException ex)
                {
                    LastError = "实例方法: " + ex.Message;
                    Debug.WriteLine("[LenovoEC] instance invoke: " + ex.Message);
                }

                if (fromMethod.HasValue) return fromMethod;

                // 有的固件把最近一次读数直接放在 Data 属性上
                try
                {
                    object? direct = instance["Data"];
                    if (direct is not null)
                    {
                        double v = Convert.ToDouble(direct);
                        if (v >= 5 && v <= 120) return v;
                    }
                }
                catch
                {
                    // 没有 Data 属性就跳过
                }
            }
        }
        catch (ManagementException ex)
        {
            LastError = "搜索实例: " + ex.Message;
            Debug.WriteLine("[LenovoEC] search: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            LastError = "拒绝访问: " + ex.Message;
            // 非管理员：联想 EC 接口可能一律拒绝访问
        }

        return null;
    }

    /// <summary>类级调用作为退路（个别机型只支持这种方式）。</summary>
    private static double? InvokeOnClass()
    {
        ManagementBaseObject? inParams = null;
        ManagementBaseObject? outParams = null;

        try
        {
            using var cls = new ManagementClass(Namespace, ClassName, null);
            inParams = cls.GetMethodParameters(MethodName);
            outParams = cls.InvokeMethod(MethodName, inParams, null);
            return PickNumber(outParams);
        }
        catch (ManagementException ex)
        {
            Debug.WriteLine("[LenovoEC] class invoke: " + ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            inParams?.Dispose();
            outParams?.Dispose();
        }
    }

    /// <summary>从方法返回对象里取出温度（不同机型字段名不同）。</summary>
    private static double? PickNumber(ManagementBaseObject? outParams)
    {
        if (outParams is null) return null;

        foreach (string name in new[] { "Data", "ReturnValue", "CPUTemp", "Temperature" })
        {
            object? value;
            try
            {
                value = outParams[name];
            }
            catch
            {
                continue;
            }

            if (value is null) continue;

            double v;
            try
            {
                v = Convert.ToDouble(value);
            }
            catch
            {
                continue;
            }

            Debug.WriteLine($"[LenovoEC] raw {name}={v}");

            // 0 表示这一代没有这个读数；合理区间之外也丢掉
            if (v >= 5 && v <= 120) return v;
        }

        return null;
    }
}
