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
    private const uint MsrTemperatureTarget = 0x1A2; // IA32_TEMPERATURE_TARGET（TjMax 在里面）
    private const uint MsrThermStatus = 0x19C;       // IA32_THERM_STATUS（每核 DTS）
    private const uint MsrPackageTherm = 0x1B1;      // IA32_PACKAGE_THERM_STATUS（封装 DTS）
    private const uint MsrPerfStatus = 0x198;        // IA32_PERF_STATUS（用来确认 ring0 到底通不通）

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

                // IA32_THERM_STATUS：bit31 = Reading Valid（1 才有效！），bits 22:16 = Digital Readout。
                // 早期版本这里判反了（把 1 当成无效），结果读到了值也照样被丢掉，温度永远为空。
                if ((eax & 0x80000000u) == 0) continue;

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
            // 每核 0x19C 都读不到时，再试"整包"温度（0x1B1），够用就行
            if (ReadMsr(MsrPackageTherm, out uint pkg) && (pkg & 0x80000000u) != 0)
            {
                int pkgDts = (int)((pkg >> 16) & 0x7F);
                double pkgTemp = tjMax - pkgDts;
                if (pkgTemp >= 1 && pkgTemp <= 130)
                {
                    max = pkgTemp;
                    average = pkgTemp;
                    LastStatus = $"ok 封装温度 TjMax={tjMax}";
                    return true;
                }
            }

            LastStatus = $"0x19C/0x1B1 都读不到（TjMax={tjMax}，ring0=open）";
            return false;
        }

        max = highest;
        average = sum / n;
        LastStatus = $"ok 核心数={n} TjMax={tjMax}";
        return true;
    }

    /// <summary>
    /// 诊断用：把几个关键 MSR 的原始值一次性抓出来。
    /// 有了这几行就能判断"到底哪一步被系统拦掉了"，不用再靠猜。
    /// </summary>
    public static List<string> Probe()
    {
        var lines = new List<string>
        {
            $"IntelDTS 类型={(Ring0Type?.FullName ?? "未找到")} ReadMsr={(ReadMsrMethod is null ? "未找到" : "有")} ring0={(IsOpen ? "open" : "closed")}"
        };

        if (!IsOpen)
        {
            lines.Add("  ring0 未打开，MSR 全部不可读（说明驱动没装上或没提权）");
            return lines;
        }

        foreach (uint msr in new[] { MsrTemperatureTarget, MsrPackageTherm, MsrPerfStatus })
        {
            lines.Add(ReadMsr(msr, out uint value)
                ? $"  MSR 0x{msr:X} = 0x{value:X8}（有效={(value >> 31) & 1} readout={(value >> 16) & 0x7F}）"
                : $"  MSR 0x{msr:X} 读取失败");
        }

        IntPtr thread = GetCurrentThread();
        IntPtr previous = SetThreadAffinityMask(thread, new IntPtr(1));
        try
        {
            foreach (int i in new[] { 0, 1, 2 })
            {
                if (SetThreadAffinityMask(thread, new IntPtr(1L << i)) == IntPtr.Zero)
                {
                    lines.Add($"  0x19C@CPU{i} 亲和性设置失败（逻辑处理器 {i} 不可用）");
                    continue;
                }

                lines.Add(ReadMsr(MsrThermStatus, out uint eax)
                    ? $"  0x19C@CPU{i} = 0x{eax:X8}（有效={(eax >> 31) & 1} readout={(eax >> 16) & 0x7F} 温度≈{LastTjMax - (int)((eax >> 16) & 0x7F)}）"
                    : $"  0x19C@CPU{i} 读取失败");
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadAffinityMask(thread, previous);
        }

        lines.Add($"  ProcessorCount={Environment.ProcessorCount} LastTjMax={LastTjMax} LastStatus={LastStatus}");
        return lines;
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
