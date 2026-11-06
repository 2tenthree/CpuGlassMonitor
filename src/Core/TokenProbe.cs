using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CpuGlassMonitor.Core;

/// <summary>
/// 把"当前进程到底有多少权限"变成可读的事实。
///
/// 起因：开机自启那次启动，日志里同时出现「elevated=True」和一连串
/// 「Requested registry access is not allowed / settings.json.tmp 被拒绝」，
/// 也就是说 IsInRole(Administrator) 说是管理员，实际却连自己的 HKCU 都写不了。
/// 光靠一个布尔值判断不了这种情况，所以这里把令牌的提权类型、完整性级别、
/// 是不是 AppContainer、以及几种关键写入到底成不成功全查一遍。
/// </summary>
internal static class TokenProbe
{
    private const int TokenElevationTypeInfo = 18;
    private const int TokenIntegrityLevelInfo = 25;
    private const int TokenIsAppContainerInfo = 29;

    private const uint TokenQuery = 0x0008;

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

    public static List<string> Describe()
    {
        var lines = new List<string>();

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            bool isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            lines.Add($"用户={identity.Name} SID={identity.User?.Value ?? "?"} IsSystem={identity.IsSystem} " +
                      $"IsInRole(管理员)={isAdmin}");
        }
        catch (Exception ex)
        {
            lines.Add("读取身份失败: " + ex.Message);
        }

        try
        {
            if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr token))
            {
                try
                {
                    if (GetTokenInformation(token, TokenElevationTypeInfo, out int type, sizeof(int), out _))
                    {
                        string name = type switch
                        {
                            1 => "Default（未提升）",
                            2 => "Full（已提升）",
                            3 => "Limited（受限令牌）",
                            _ => "未知(" + type + ")"
                        };
                        lines.Add("提权类型=" + name);
                    }

                    if (GetTokenInformation(token, TokenIsAppContainerInfo, out int container, sizeof(int), out _))
                    {
                        lines.Add("AppContainer=" + (container != 0));
                    }

                    lines.Add("完整性级别=" + ReadIntegrity(token));
                }
                finally
                {
                    CloseHandle(token);
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("读取令牌失败: " + ex.Message);
        }

        lines.Add("可写 %APPDATA%=" + TryWriteDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CpuGlassMonitor")));

        lines.Add("可写 程序目录=" + TryWriteDir(AppContext.BaseDirectory));

        lines.Add("可写 HKCU\\...\\AppCompatFlags\\Layers=" + TryOpenRegistry(
            Microsoft.Win32.Registry.CurrentUser,
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"));

        lines.Add("可写 HKLM\\SYSTEM\\CurrentControlSet\\Services=" + TryOpenRegistry(
            Microsoft.Win32.Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services"));

        return lines;
    }

    private static string ReadIntegrity(IntPtr token)
    {
        try
        {
            GetTokenInformation(token, TokenIntegrityLevelInfo, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return "未知";

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelInfo, buffer, needed, out _)) return "未知";

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (!ConvertSidToStringSid(sid, out IntPtr sidString)) return "未知";
                try
                {
                    // 完整性 SID 的最后一段 RID 决定级别：0x1000 低 / 0x2000 中 / 0x3000 高 / 0x4000 系统
                    int subAuthorityCount = Marshal.ReadByte(sid, 1);
                    int lastRid = Marshal.ReadInt32(sid, 8 + (subAuthorityCount - 1) * 4);
                    string level = lastRid switch
                    {
                        0x0000 => "Untrusted",
                        0x1000 => "低",
                        0x2000 => "中",
                        0x2100 => "中+",
                        0x3000 => "高",
                        0x4000 => "System",
                        _ => "0x" + lastRid.ToString("X")
                    };
                    return level;
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
            return "未知";
        }
    }

    private static string TryWriteDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, ".write-probe");
            File.WriteAllText(file, "x");
            File.Delete(file);
            return "是";
        }
        catch (Exception ex)
        {
            return "否（" + ex.GetType().Name + ": " + ex.Message + "）";
        }
    }

    private static string TryOpenRegistry(Microsoft.Win32.RegistryKey root, string path)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(path, writable: true);
            return key == null ? "否（键不存在）" : "是";
        }
        catch (Exception ex)
        {
            return "否（" + ex.GetType().Name + ": " + ex.Message + "）";
        }
    }
}
