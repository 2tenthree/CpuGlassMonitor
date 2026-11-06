using System.Diagnostics;
using System.Management;

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
                    lock (_gate)
                    {
                        _value = t.Value;
                        _stampTicks = DateTime.UtcNow.Ticks;
                        _status = $"Lenovo EC {t.Value:F0}°C";
                    }

                    failures = 0;
                }
                else
                {
                    failures++;
                    if (failures >= 5)
                    {
                        lock (_gate)
                        {
                            _status = "Lenovo EC 不可用";
                        }

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
        ManagementBaseObject? inParams = null;
        ManagementBaseObject? outParams = null;

        try
        {
            using var cls = new ManagementClass(Namespace, ClassName, null);
            inParams = cls.GetMethodParameters(MethodName);
            outParams = cls.InvokeMethod(MethodName, inParams, null);

            if (outParams is null) return null;

            object? data = null;
            try
            {
                data = outParams["Data"];
            }
            catch
            {
                // 有的机型直接返回方法返回值
            }

            data ??= outParams["ReturnValue"];

            if (data is null) return null;

            double v = Convert.ToDouble(data);
            Debug.WriteLine($"[LenovoEC] raw={v}");

            // 0 表示这一代没有这个读数；合理区间之外也丢掉
            if (v < 5 || v > 120) return null;
            return v;
        }
        catch (ManagementException ex)
        {
            Debug.WriteLine("[LenovoEC] " + ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // 非管理员：联想 EC 接口一律拒绝访问，直接放弃，别每秒钟试一遍
            return null;
        }
        finally
        {
            inParams?.Dispose();
            outParams?.Dispose();
        }
    }
}
