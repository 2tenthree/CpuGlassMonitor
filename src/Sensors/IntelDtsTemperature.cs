using System.Reflection;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace CpuGlassMonitor.Sensors;

/// <summary>
/// 自己读 Intel DTS（数字温度传感器）的兜底实现。
///
/// 为什么不直接用 LHM 的温度：LibreHardwareMonitor 的 Intel 温度是「先读 MSR 0x1A2
/// 拿到 TjMax，再读每核 MSR 0x19C 算差值」，其中 0x1A2 这一步只要失败，所有核心温度就都是 null
/// （表现为「频率/电压都正常，就是温度 —」）。在开了 VBS / 内存完整性、或刷了新微码的机器上
/// 经常出现这种情况。
///
/// 这里复用 LHM 已经装好的那个 ring0 驱动（LibreHardwareMonitor.Hardware.Ring0，内部类型，
/// 用反射调），直接读 0x19C；0x1A2 读不到就按 Intel 移动端惯例假定 TjMax = 100°C。
/// 读不到任何核心时返回 false，让上层继续走别的兜底。
/// </summary>
internal static class IntelDtsTemperature
{
    private const uint MsrTemperatureTarget = 0x1A2; // IA32_TEMPERATURE_TARGET
    private const uint MsrThermStatus = 0x19C;       // IA32_THERM_STATUS

    private static readonly Type? Ring0Type =
        typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Ring0");

    private static readonly PropertyInfo? IsOpenProperty = Ring0Type?.GetProperty(
        "IsOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly MethodInfo? ReadMsrMethod = Ring0Type?.GetMethod(
        "ReadMsr",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
        null,
        new[] { typeof(uint), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() },
        null);

    public static int LastTjMax { get; private set; }

    public static string LastStatus { get; private set; } = "未尝试";

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

    private static bool IsOpen
    {
        get
        {
            try
            {
                return IsOpenProperty?.GetValue(null) is bool open && open;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool ReadMsr(uint index, out uint eax)
    {
        eax = 0;
        if (ReadMsrMethod is null) return false;

        object?[] args = { index, 0u, 0u };
        try
        {
            bool ok = ReadMsrMethod.Invoke(null, args) is bool b && b;
            if (ok) eax = (uint)args[1]!;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>返回 (最高温度, 平均温度)。读不到返回 false。</summary>
    public static bool TryRead(out double max, out double average)
    {
        max = 0;
        average = 0;

        if (!IsOpen)
        {
            LastStatus = "ring0 未打开";
            return false;
        }

        int tjMax = 100;
        if (ReadMsr(MsrTemperatureTarget, out uint target))
        {
            uint value = (target >> 16) & 0xFF;
            if (value >= 40 && value <= 125) tjMax = (int)value;
        }

        LastTjMax = tjMax;

        int count = Environment.ProcessorCount;
        if (count > 64) count = 64;

        IntPtr thread = GetCurrentThread();
        IntPtr previous = SetThreadAffinityMask(thread, new IntPtr(1));

        double sum = 0;
        int n = 0;
        double highest = double.MinValue;

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (SetThreadAffinityMask(thread, new IntPtr(1L << i)) == IntPtr.Zero) continue;
                if (!ReadMsr(MsrThermStatus, out uint eax)) continue;

                if ((eax & 0x80000000u) != 0) continue; // 这一核没报有效值
                int dts = (int)((eax >> 16) & 0x7F);
                double temp = tjMax - dts;
                if (temp < 1 || temp > 130) continue;

                sum += temp;
                n++;
                if (temp > highest) highest = temp;
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadAffinityMask(thread, previous);
        }

        if (n == 0)
        {
            LastStatus = $"0x19C 读不到（TjMax={tjMax}）";
            return false;
        }

        max = highest;
        average = sum / n;
        LastStatus = $"ok 核心数={n} TjMax={tjMax}";
        return true;
    }

    /// <summary>诊断用：把原始 MSR 读数写进日志行。</summary>
    public static void Describe(List<string> lines)
    {
        lines.Add($"IntelDTS: ring0={(IsOpen ? "open" : "closed")} {LastStatus}");

        if (!IsOpen) return;

        lines.Add(ReadMsr(MsrTemperatureTarget, out uint target)
            ? $"  MSR 0x1A2 = 0x{target:X8} (TjMax 字段={(target >> 16) & 0xFF})"
            : "  MSR 0x1A2 读取失败");

        IntPtr thread = GetCurrentThread();
        IntPtr previous = SetThreadAffinityMask(thread, new IntPtr(1));
        try
        {
            int count = Math.Min(Environment.ProcessorCount, 64);
            for (int i = 0; i < count; i++)
            {
                if (SetThreadAffinityMask(thread, new IntPtr(1L << i)) == IntPtr.Zero) continue;
                lines.Add(ReadMsr(MsrThermStatus, out uint eax)
                    ? $"  CPU{i} 0x19C = 0x{eax:X8} DTS={(eax >> 16) & 0x7F}"
                    : $"  CPU{i} 0x19C 读取失败");
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadAffinityMask(thread, previous);
        }
    }
}
