using System.Diagnostics;

namespace CpuGlassMonitor.Core;

/// <summary>
/// 检查可执行文件本身的「强制完整性标签」（Mandatory Integrity Label）。
///
/// Windows 创建进程时，新进程的完整性级别 = min(调用方令牌 IL, 映像文件标签 IL)。
/// 所以只要 exe（或它继承标签的父目录）被打了 Low Mandatory Level，
/// 计划任务就算写着 HighestAvailable、令牌也是 Full 已提升，进程依然会被压成低 IL。
/// 这时 HKLM Services / HKCU / %APPDATA% 全写不了：ring0 驱动装不上、CPU 参数全空、
/// 托盘通知被 UIPI 丢弃。表现和"自启没提权"一模一样，但根因在文件系统 ACL，
/// 提权重启、看门狗、托盘重挂都救不了，只能把程序放到没有该标签的目录，
/// 或由管理员执行 icacls /setintegritylevel 把标签去掉。
///
/// 实测场景：D:\bin\WorkSpace\DSH 被显式设了 Low Mandatory Level:(OI)(CI)(NW)，
/// 整个项目目录树继承，dist 里的 exe 全部带 (I)(NW) 低标签。
/// </summary>
internal static class ImageLabel
{
    /// <summary>推荐的安装目录：优先返回确实放着程序、且不带低标签的候选目录。</summary>
    public static string SafeDirectory
    {
        get
        {
            foreach (string dir in CandidateDirectories)
            {
                if (File.Exists(Path.Combine(dir, "CpuGlassMonitor.exe"))) return dir;
            }
            return CandidateDirectories[0];
        }
    }

    /// <summary>
    /// 可以安全承载本程序的目录，按优先级排列。
    /// 允许用环境变量 CPUGLASS_HOME 指定自己习惯的位置；其余两个是本项目实际部署过的位置
    /// （D:\SoftWare\CpuGlassMonitor 是用户选定的「好找」目录，用户级 Programs 目录是早期默认值）。
    /// 调用方还会再查一次候选 exe 有没有低完整性标签，光有文件不算数。
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories { get; } = BuildCandidates();

    private static string[] BuildCandidates()
    {
        var list = new List<string>();
        string? custom = Environment.GetEnvironmentVariable("CPUGLASS_HOME");
        if (!string.IsNullOrWhiteSpace(custom)) list.Add(custom.Trim());

        list.Add(@"D:\SoftWare\CpuGlassMonitor");
        list.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "CpuGlassMonitor"));

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? _cachedLevel;

    /// <summary>返回「低 / 中 / 高 / 系统 / 无 / 未知」。</summary>
    /// <remarks>
    /// 用 icacls 而不是托管 API：低 IL 进程读 SACL 会被拒，而 icacls 能读标签；
    /// 标签名是固定英文（Low/Medium/High Mandatory Level），不受系统语言影响。
    /// 一次启动只查一次，避免自动启动自检重复拉起子进程。
    /// </remarks>
    public static string Describe(string? exePath = null)
    {
        if (exePath == null && _cachedLevel != null) return _cachedLevel;
        string level = DescribeCore(exePath);
        if (exePath == null) _cachedLevel = level;
        return level;
    }

    private static string DescribeCore(string? exePath)
    {
        string path = exePath ?? Environment.ProcessPath ?? "";
        if (path.Length == 0 || !File.Exists(path)) return "未知";

        try
        {
            var psi = new ProcessStartInfo("icacls.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(path);

            using Process? p = Process.Start(psi);
            if (p == null) return "未知";
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { }
                return "未知";
            }

            return FromIcacls(output);
        }
        catch
        {
            return "未知";
        }
    }

    public static bool IsLow(string? exePath = null) => Describe(exePath) == "低";

    /// <summary>给日志/诊断报告用的一句话结论。</summary>
    public static string Conclusion(string level) => level switch
    {
        "低" => "exe 带低完整性标签 → 计划任务最高权限也会被压成低 IL，必须把程序移到普通目录（" + SafeDirectory + "），或由管理员对目录执行 icacls /setintegritylevel H",
        "未知" => "读不到文件完整性标签（权限不足）；若令牌是「已提升」却仍是低 IL，请优先检查 exe/父目录的低完整性标签",
        _ => "文件标签=" + level
    };

    private static string FromIcacls(string output)
    {
        if (output.Contains("Low Mandatory Level", StringComparison.OrdinalIgnoreCase)) return "低";
        if (output.Contains("Medium Mandatory Level", StringComparison.OrdinalIgnoreCase)) return "中";
        if (output.Contains("High Mandatory Level", StringComparison.OrdinalIgnoreCase)) return "高";
        if (output.Contains("System Mandatory Level", StringComparison.OrdinalIgnoreCase)) return "系统";
        if (output.Contains("Mandatory Label", StringComparison.OrdinalIgnoreCase)) return "未知";
        return "无";
    }
}
