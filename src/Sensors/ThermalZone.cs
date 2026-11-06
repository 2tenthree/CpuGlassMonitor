using System.Management;
using CpuGlassMonitor.Core;

namespace CpuGlassMonitor.Sensors;

/// <summary>
/// ACPI 热区温度兜底。
/// LibreHardwareMonitor 读 CPU 温度靠 ring0 驱动直接读 MSR（0x1A2 取 TjMax、0x19C 取每核温度）。
/// 当系统开了「内核隔离 / 内存完整性」、装了 Hyper-V / WSL2 等把 VBS 打开时，
/// hypervisor 会把这类 MSR 拦住，LHM 就只能报 null。
/// 这时退一步用 ACPI 热区（WMI MSAcpi_ThermalZoneTemperature）拿一个粗粒度温度，
/// 数值不如 MSR 精细，但至少能看出"是不是在过热"。
/// </summary>
internal sealed class ThermalZone
{
    private readonly ManagementObjectSearcher? _searcher;
    private DateTime _lastQuery = DateTime.MinValue;
    private double? _lastValue;
    private bool _failed;

    public ThermalZone()
    {
        try
        {
            _searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        }
        catch (Exception ex)
        {
            _failed = true;
            SettingsStore.Log("热区查询初始化失败: " + ex.Message);
        }
    }

    /// <summary>返回摄氏度；读不到返回 null。内部有 2 秒节流，WMI 查询比较慢。</summary>
    public double? Read()
    {
        if (_failed || _searcher == null) return null;
        if ((DateTime.UtcNow - _lastQuery).TotalSeconds < 2.0) return _lastValue;

        _lastQuery = DateTime.UtcNow;
        try
        {
            double hottest = double.MinValue;
            foreach (ManagementBaseObject item in _searcher.Get())
            {
                try
                {
                    double celsius = Convert.ToDouble(item["CurrentTemperature"]) / 10.0 - 273.15;
                    if (celsius > hottest) hottest = celsius;
                }
                finally
                {
                    item.Dispose();
                }
            }

            _lastValue = hottest > -40 && hottest < 150 ? hottest : null;
        }
        catch (Exception ex)
        {
            _failed = true;
            _lastValue = null;
            SettingsStore.Log("热区温度读取失败: " + ex.Message);
        }

        return _lastValue;
    }

    /// <summary>诊断用：把这次 WMI 查询的真实结果（条数/异常）描述出来。</summary>
    public string Describe()
    {
        if (_searcher == null) return "热区: 查询器没建成";
        try
        {
            var parts = new List<string>();
            foreach (ManagementBaseObject item in _searcher.Get())
            {
                try
                {
                    double celsius = Convert.ToDouble(item["CurrentTemperature"]) / 10.0 - 273.15;
                    parts.Add(celsius.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "°C");
                }
                finally
                {
                    item.Dispose();
                }
            }

            return parts.Count == 0 ? "热区: 0 个实例" : "热区: " + string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return "热区: 读取异常 " + ex.GetType().Name + ": " + ex.Message;
        }
    }
}
