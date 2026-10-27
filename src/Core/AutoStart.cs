using System.Diagnostics;

namespace CpuGlassMonitor.Core;

/// <summary>
/// 开机自启：优先用计划任务（最高权限，登录时不弹 UAC），失败则退回注册表 Run。
/// 状态以"实际注册情况"为准（计划任务或 Run 键任一存在即为开启），
/// 设置里的 WithWindows 只是这份状态的镜像。
/// </summary>
public static class AutoStart
{
    public const string TaskName = "CpuGlassMonitor";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CpuGlassMonitor";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CpuGlassMonitor.exe");

    /// <summary>当前是否真的注册了开机自启。</summary>
    public static bool IsRegistered => TaskExists() || RunKeyExists();

    public static bool IsEnabled => File.Exists(Environment.ProcessPath ?? "") && IsRegistered;

    /// <summary>把开机自启对齐到期望状态；返回是否成功，message 可直接展示给用户。</summary>
    public static bool Sync(bool enabled, out string message)
    {
        if (enabled)
        {
            if (IsRegistered)
            {
                message = "开机自启已经是开启状态。";
                return true;
            }

            return Enable(out message);
        }

        if (!IsRegistered)
        {
            message = "开机自启已经是关闭状态。";
            return true;
        }

        return Disable(out message);
    }

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

        var problems = new List<string>();
        if (TaskExists())
        {
            if (!TryDeleteTask(out string taskError))
            {
                // 计划任务是以 HIGHEST 创建的，非管理员身份删不掉，尝试提权删除一次
                if (TryDeleteTaskElevated(out string elevatedError))
                {
                    SettingsStore.Log("开机自启：已通过提权删除计划任务。");
                }
                else
                {
                    problems.Add(string.IsNullOrWhiteSpace(elevatedError) ? taskError : elevatedError);
                }
            }

            // schtasks 说"找不到任务"但文件还在（残留/损坏的定义）时，直接删文件兜底
            if (TaskExists())
            {
                try
                {
                    File.Delete(TaskFilePath);
                    SettingsStore.Log("开机自启：已删除残留的计划任务定义文件。");
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("开机自启：删除任务定义文件失败 - " + ex.Message);
                }
            }
        }

        RemoveRunKey();

        if (TaskExists() || RunKeyExists())
        {
            string detail = problems.Count > 0 ? "（" + string.Join("；", problems) + "）" : "";
            message = "取消开机自启失败" + detail + "：请以管理员身份运行本程序后再取消。";
            return false;
        }

        message = "已取消开机自启。";
        return true;
    }

    public static bool TaskExists()
    {
        // 优先看任务定义文件：不启动子进程，避免 schtasks 卡住把界面拖死；
        // 计划任务就注册在根目录下，文件路径固定。
        try
        {
            if (File.Exists(TaskFilePath))
            {
                return true;
            }
        }
        catch
        {
            // 忽略，退回 schtasks 查询
        }

        (int code, _) = Run("schtasks.exe", new[] { "/Query", "/TN", TaskName });
        return code == 0;
    }

    /// <summary>计划任务的定义文件（%SystemRoot%\System32\Tasks\任务名）。</summary>
    public static string TaskFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks", TaskName);

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

    /// <summary>提权删除计划任务（当前进程不是管理员时的兜底，会弹一次 UAC）。</summary>
    private static bool TryDeleteTaskElevated(out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/Delete /TN \"{TaskName}\" /F"
            };

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                error = "无法启动提权进程。";
                return false;
            }

            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
        // 外层再加一道总超时：哪怕 Process.Start/管道读写卡住，也不会把界面拖死
        Task<(int Code, string Output)> task = Task.Run(() => RunCore(file, args));
        if (task.Wait(6000))
        {
            return task.Result;
        }

        return (-1, $"{file} 调用超时");
    }

    private static (int Code, string Output) RunCore(string file, IEnumerable<string> args)
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

            // 不能在 WaitForExit 之前同步 ReadToEnd：子进程不退出时会永远卡在这里
            // （曾经导致「开机自启」状态查询把整个界面线程/后台任务挂死）。
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000))
            {
                try
                {
                    p.Kill(true);
                }
                catch
                {
                    // 忽略
                }

                return (-1, "schtasks 超时");
            }

            string output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
