using System.Diagnostics;

namespace CpuGlassMonitor.Core;

/// <summary>
/// 开机自启：优先用计划任务（最高权限，登录时不弹 UAC），失败则退回注册表 Run。
/// </summary>
public static class AutoStart
{
    public const string TaskName = "CpuGlassMonitor";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CpuGlassMonitor";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CpuGlassMonitor.exe");

    public static bool IsEnabled =>
        File.Exists(Environment.ProcessPath ?? "") && (TaskExists() || RunKeyExists());

    public static bool Enable(out string message)
    {
        message = "";

        if (TryCreateTask(out string taskError))
        {
            RemoveRunKey();
            message = "已通过计划任务设置开机自启（登录时不弹 UAC）。";
            return true;
        }

        if (TryWriteRunKey(out string runError))
        {
            message = "计划任务创建失败，已改用注册表自启：每次登录可能弹出 UAC 提示。";
            return true;
        }

        message = "设置失败：" + taskError + " / " + runError;
        return false;
    }

    public static bool Disable(out string message)
    {
        message = "";
        bool taskOk = TryDeleteTask(out string taskError);
        RemoveRunKey();
        if (taskOk)
        {
            message = "已取消开机自启。";
            return true;
        }

        message = string.IsNullOrEmpty(taskError) ? "已取消开机自启。" : "取消计划任务失败：" + taskError;
        return true;
    }

    public static bool TaskExists()
    {
        (int code, _) = Run("schtasks.exe", new[] { "/Query", "/TN", TaskName });
        return code == 0;
    }

    public static bool RunKeyExists()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateTask(out string error)
    {
        string command = $"\"{ExecutablePath}\" --autostart";
        (int code, string output) = Run("schtasks.exe", new[]
        {
            "/Create", "/TN", TaskName, "/TR", command, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"
        });
        error = code == 0 ? "" : output.Trim();
        return code == 0;
    }

    private static bool TryDeleteTask(out string error)
    {
        (int code, string output) = Run("schtasks.exe", new[] { "/Delete", "/TN", TaskName, "/F" });
        error = code == 0 ? "" : output.Trim();
        return code == 0;
    }

    private static bool TryWriteRunKey(out string error)
    {
        error = "";
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey)!;
            key.SetValue(RunValue, $"\"{ExecutablePath}\" --autostart");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch
        {
            // 忽略
        }
    }

    private static (int Code, string Output) Run(string file, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return (p.HasExited ? p.ExitCode : -1, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
