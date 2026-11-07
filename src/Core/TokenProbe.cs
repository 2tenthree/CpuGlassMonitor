using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CpuGlassMonitor.Core;

/// <summary>
/// 把"当前进程到底有多少权限"变成可读、可判断的事实，并给出能不能用的结论。
///
/// 起因：开机自启那次启动，日志里同时出现「IsInRole(管理员)=True」和一连串
/// 「Requested registry access is not allowed / settings.json.tmp 被拒绝」，
/// 也就是说光靠 IsInRole(Administrator) 判断会严重误判：令牌里带着管理员组 SID，
/// 完整性级别却只有「低」，实际连自己的 HKCU 都写不了。
///
/// 低完整性令牌会造成三件事，正好就是"开机自启坏了"的全部症状：
///   1) 写不了 HKLM\...\Services / 调不动 SCM → ring0 驱动装不上、MSR 读不了 → CPU 参数全空；
///   2) 写不了 HKCU\Control Panel\NotifyIconSettings → Win11 把托盘图标折叠，看起来像没图标；
///      更底层的是低 IL 进程给 explorer（中 IL）发的 Shell_NotifyIcon 会被 UIPI 直接丢掉；
///   3) 写不了 %APPDATA% → 配置保存失败、日志只能降级到程序目录。
///
/// 所以 IsElevated 不能用 IsInRole，必须用「实测能力」。
/// </summary>
internal static class TokenProbe
{
    private const int TokenElevationTypeInfo = 18;
    private const int TokenIntegrityLevelInfo = 25;
    private const int TokenIsAppContainerInfo = 29;

    private const uint TokenQuery = 0x0008;

    /// <summary>完整性级别 RID：0x2000 = 中，0x3000 = 高。</summary>
    public const int IntegrityMedium = 0x2000;
    public const int IntegrityHigh = 0x3000;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int infoClass, out int info, int infoLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public sealed record Result(
        string Identity,
        bool AdminRole,
        int ElevationType,
        int IntegrityRid,
        bool AppContainer,
        bool CanWriteAppData,
        string AppDataDetail,
        bool CanWriteProgramDir,
        string ProgramDirDetail,
        bool CanWriteHkcu,
        string HkcuDetail,
        bool CanWriteServices,
        string ServicesDetail)
    {
        public string ElevationName => ElevationType switch
        {
            1 => "Default（未提升）",
            2 => "Full（已提升）",
            3 => "Limited（受限令牌）",
            _ => "未知(" + ElevationType + ")"
        };

        public string IntegrityName => IntegrityRid switch
        {
            0x0000 => "Untrusted",
            0x1000 => "低",
            IntegrityMedium => "中",
            0x2100 => "中+",
            IntegrityHigh => "高",
            0x4000 => "System",
            _ => "0x" + IntegrityRid.ToString("X")
        };

        /// <summary>
        /// 这个令牌能不能真正干活：装得动 ring0 驱动、写得了 HKCU、存得下配置。
        /// 注意不能只看完整性级别：某些机器上高 IL 也会被策略拦住写注册表，所以四项实测都要过。
        /// </summary>
        public bool Usable => AdminRole
                              && IntegrityRid >= IntegrityHigh
                              && CanWriteAppData
                              && CanWriteHkcu
                              && CanWriteServices;

        public string FailureReason
        {
            get
            {
                var reasons = new List<string>();
                if (!AdminRole) reasons.Add("令牌未提升（不在管理员组）");
                if (IntegrityRid < IntegrityHigh) reasons.Add($"完整性级别只有「{IntegrityName}」，需要「高」");
                if (!CanWriteServices) reasons.Add("HKLM Services 不可写（装不了 ring0 驱动）");
                if (!CanWriteHkcu) reasons.Add("HKCU 不可写（托盘图标没法常显）");
                if (!CanWriteAppData) reasons.Add("%APPDATA% 不可写（配置存不下）");
                return reasons.Count == 0 ? "可用" : string.Join("；", reasons);
            }
        }
    }

    private static Result? _cached;

    /// <summary>一次启动只探测一次（会短暂创建/删除探针文件）。</summary>
    public static Result Probe() => _cached ??= ProbeCore();

    public static List<string> Describe() => Describe(Probe());

    public static List<string> Describe(Result r) =>
    [
        r.Identity,
        "提权类型=" + r.ElevationName,
        "AppContainer=" + r.AppContainer,
        "完整性级别=" + r.IntegrityName,
        "可写 %APPDATA%=" + r.AppDataDetail,
        "可写 程序目录=" + r.ProgramDirDetail,
        @"可写 HKCU\...\AppCompatFlags\Layers=" + r.HkcuDetail,
        @"可写 HKLM\SYSTEM\CurrentControlSet\Services=" + r.ServicesDetail,
        "令牌可用=" + r.Usable + " 原因=" + r.FailureReason
    ];

    private static Result ProbeCore()
    {
        string identity;
        bool admin;
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            identity = $"用户={id.Name} SID={id.User?.Value ?? "?"} IsSystem={id.IsSystem} " +
                       $"IsInRole(管理员)={admin}";
        }
        catch (Exception ex)
        {
            admin = false;
            identity = "读取身份失败: " + ex.Message;
        }

        int elevation = 0;
        int integrity = 0;
        bool container = false;
        try
        {
            if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr token))
            {
                try
                {
                    if (GetTokenInformation(token, TokenElevationTypeInfo, out int type, sizeof(int), out _)) elevation = type;
                    if (GetTokenInformation(token, TokenIsAppContainerInfo, out int c, sizeof(int), out _)) container = c != 0;
                    integrity = ReadIntegrity(token);
                }
                finally
                {
                    CloseHandle(token);
                }
            }
        }
        catch
        {
            // 探测失败按"不可用"处理，不因为诊断代码影响启动
        }

        (bool appDataOk, string appDataDetail) = TryWriteDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CpuGlassMonitor"));
        (bool programOk, string programDetail) = TryWriteDir(AppContext.BaseDirectory);
        (bool hkcuOk, string hkcuDetail) = TryOpenRegistry(
            Microsoft.Win32.Registry.CurrentUser,
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
        (bool svcOk, string svcDetail) = TryOpenRegistry(
            Microsoft.Win32.Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services");

        return new Result(identity, admin, elevation, integrity, container,
            appDataOk, appDataDetail, programOk, programDetail,
            hkcuOk, hkcuDetail, svcOk, svcDetail);
    }

    private static int ReadIntegrity(IntPtr token)
    {
        try
        {
            GetTokenInformation(token, TokenIntegrityLevelInfo, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return 0;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelInfo, buffer, needed, out _)) return 0;

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (!ConvertSidToStringSid(sid, out IntPtr sidString)) return 0;
                try
                {
                    // 完整性 SID 的最后一段 RID 决定级别：0x1000 低 / 0x2000 中 / 0x3000 高 / 0x4000 系统
                    int subAuthorityCount = Marshal.ReadByte(sid, 1);
                    return Marshal.ReadInt32(sid, 8 + (subAuthorityCount - 1) * 4);
                }
                finally
                {
                    LocalFree(sidString);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return 0;
        }
    }

    private static (bool Ok, string Detail) TryWriteDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, ".write-probe");
            File.WriteAllText(file, "x");
            File.Delete(file);
            return (true, "是");
        }
        catch (Exception ex)
        {
            return (false, "否（" + ex.GetType().Name + ": " + ex.Message + "）");
        }
    }

    private static (bool Ok, string Detail) TryOpenRegistry(Microsoft.Win32.RegistryKey root, string path)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(path, writable: true);
            return key == null ? (false, "否（键不存在）") : (true, "是");
        }
        catch (Exception ex)
        {
            return (false, "否（" + ex.GetType().Name + ": " + ex.Message + "）");
        }
    }
}
