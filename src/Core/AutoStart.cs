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

    /// <summary>
    /// 自启项应该指向的 exe。
    /// 当前程序若坐在带低完整性标签的目录里（例如某个被沙箱化的工作区），
    /// 计划任务就算 HighestAvailable 也会被系统压成低 IL，CPU/托盘全废。
    /// 这时按优先级找一份「存在且不带低标签」的副本；程序搬过家也没关系，
    /// 候选目录由 ImageLabel.CandidateDirectories 统一维护（支持 CPUGLASS_HOME 覆盖）。
    /// </summary>
    public static string PreferredExe
    {
        get
        {
            string exe = ExecutablePath;
            if (!ImageLabel.IsLow(exe)) return exe;

            foreach (string dir in ImageLabel.CandidateDirectories)
            {
                string candidate = Path.Combine(dir, Path.GetFileName(exe));
                if (File.Exists(candidate) && !ImageLabel.IsLow(candidate)) return candidate;
            }

            return exe;
        }
    }

    /// <summary>当前是否真的注册了开机自启。</summary>
    public static bool IsRegistered => TaskExists() || RunKeyExists();

    public static bool IsEnabled => File.Exists(Environment.ProcessPath ?? "") && IsRegistered;

    /// <summary>把开机自启对齐到期望状态；返回是否成功，message 可直接展示给用户。</summary>
    public static bool Sync(bool enabled, out string message, bool allowElevationPrompt = true)
    {
        if (enabled)
        {
            if (IsRegistered)
            {
                message = "开机自启已经是开启状态。";
                return true;
            }

            return Enable(out message, allowElevationPrompt);
        }

        if (!IsRegistered)
        {
            message = "开机自启已经是关闭状态。";
            return true;
        }

        return Disable(out message);
    }

    public static bool Enable(out string message, bool allowElevationPrompt = true)
    {
        message = "";

        if (TryCreateTask(out string taskError))
        {
            RemoveRunKey();
            message = "已通过计划任务设置开机自启（登录时不弹 UAC，带管理员权限）。";
            return true;
        }

        // 计划任务要管理员才能建（/RL HIGHEST）。这里主动提权建一次：
        // 建好以后每次登录都是最高权限，读得到 CPU 温度/频率/电压，也不会弹 UAC。
        string elevatedError = "已跳过提权弹窗";
        if (allowElevationPrompt && TryCreateTaskElevated(out elevatedError))
        {
            RemoveRunKey();
            message = "已通过计划任务设置开机自启（登录时不弹 UAC，带管理员权限）。";
            return true;
        }

        if (TryWriteRunKey(out string runError))
        {
            message = "计划任务创建失败，已改用注册表自启：开机启动时没有管理员权限，"
                + "CPU 温度 / 频率 / 电压会显示不出来。建议以管理员身份运行本程序后重新勾选一次。";
            return true;
        }

        message = "设置失败：" + taskError + " / " + elevatedError + " / " + runError;
        return false;
    }

    /// <summary>
    /// 启动时自检：把"能开机自启但没有管理员权限"的旧状态修正过来。
    /// 只在已提权时动手（否则建不了最高权限任务）。
    /// 返回是否做了修改，便于写日志。
    /// </summary>
    public static bool EnsureElevatedAutostart(bool isElevated, out string note)
    {
        note = "";
        if (!isElevated || !IsRegistered) return false;

        if (TaskExists())
        {
            if (TaskRunsElevated()) return false;

            // 任务存在但不是最高权限：重建一次
            TryDeleteTask(out _);
            if (TryCreateTask(out _))
            {
                note = "开机自启：计划任务已升级为最高权限（登录后可直接读 CPU 温度/频率/电压）。";
                SettingsStore.Log(note);
                return true;
            }

            return false;
        }

        if (!RunKeyExists()) return false;

        // 只有注册表自启（没有计划任务）：换成最高权限计划任务，开机才不会掉权限
        if (TryCreateTask(out _))
        {
            RemoveRunKey();
            note = "开机自启：已从注册表自启升级为计划任务（带管理员权限，登录不弹 UAC）。";
            SettingsStore.Log(note);
            return true;
        }

        SettingsStore.Log("开机自启：计划任务创建失败，继续保持注册表自启（开机无管理员权限）。");
        return false;
    }

    /// <summary>
    /// 自启项可能还指着旧的 exe 路径（换过目录、或换过发布文件夹）。
    /// 那样开机启动的是旧副本（甚至是不存在的文件），现象就是"开机后没起来"。
    /// 这里把指向不对的自启项更新成当前程序路径。
    /// </summary>
    public static bool EnsureTargetsCurrentExe(out string note)
    {
        note = "";
        try
        {
            // 目标用 PreferredExe：当前 exe 带低标签时，任务应指向无标签目录里的副本。
            // 否则这里会把"指向副本"误判成"指向旧路径"，又给改回带标签的 exe。
            string exe = PreferredExe;

            if (TaskExists())
            {
                string xml = File.Exists(TaskFilePath) ? File.ReadAllText(TaskFilePath) : "";
                if (xml.Length == 0) return false;

                bool wrongPath = !xml.Contains(exe, StringComparison.OrdinalIgnoreCase);
                // 老版本建的任务没有延迟、而且默认"电池上不启动"——这两条都会让开机自启失效
                bool oldShape = !xml.Contains("<Delay>", StringComparison.OrdinalIgnoreCase)
                                || xml.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase);

                if (!wrongPath && !oldShape) return false;
                if (!TryCreateTask(out _)) return false;

                note = oldShape
                    ? $"开机自启：计划任务已升级（登录后延迟 {StartupDelaySeconds} 秒、电池上也启动），目标={exe}。"
                    : $"开机自启：计划任务目标已更新为 {exe}（当前 exe 带低完整性标签时指向无标签副本）。";
                SettingsStore.Log(note);
                return true;
            }

            if (!RunKeyExists()) return false;

            string current = "";
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                current = key?.GetValue(RunValue) as string ?? "";
            }
            catch
            {
                return false;
            }

            if (current.Contains(exe, StringComparison.OrdinalIgnoreCase)) return false;
            if (!TryWriteRunKey(out _)) return false;

            note = "开机自启：注册表项原来指向旧路径，已更新为当前程序。";
            SettingsStore.Log(note);
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log("检查自启路径失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>把计划任务 / 注册表项的实际内容摘出来，供诊断报告使用。</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine("TaskExists=" + TaskExists() + " TaskRunsElevated=" + TaskRunsElevated());
            if (File.Exists(TaskFilePath))
            {
                string xml = File.ReadAllText(TaskFilePath);
                string runLevel = System.Text.RegularExpressions.Regex.Match(xml, "<RunLevel>(.*?)</RunLevel>") is { Success: true } m1 ? m1.Groups[1].Value : "-";
                string command = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>") is { Success: true } m2 ? m2.Groups[1].Value : "-";
                string args = System.Text.RegularExpressions.Regex.Match(xml, "<Arguments>(.*?)</Arguments>") is { Success: true } m3 ? m3.Groups[1].Value : "-";
                sb.AppendLine($"任务 RunLevel={runLevel}");
                sb.AppendLine($"任务 Command={command}");
                sb.AppendLine($"任务 Arguments={args}");
                sb.AppendLine("任务指向自启目标=" + (command.Contains(PreferredExe, StringComparison.OrdinalIgnoreCase) ? "是" : "否 → " + PreferredExe));
            }
            else
            {
                sb.AppendLine("任务文件不存在: " + TaskFilePath);
            }

            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                object? v = key?.GetValue("CpuGlassMonitor");
                sb.AppendLine("Run 键=" + (v?.ToString() ?? "无"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("Run 键读取失败: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Describe 失败: " + ex.Message);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>计划任务是否以最高权限运行（读任务定义 XML 判断，不启子进程）。</summary>
    public static bool TaskRunsElevated()
    {
        try
        {
            if (!File.Exists(TaskFilePath)) return false;
            string xml = File.ReadAllText(TaskFilePath);
            return xml.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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

    /// <summary>
    /// 计划任务的 XML 定义。
    /// 用 XML 而不是 `schtasks /SC ONLOGON` 的原因是命令行建不出这几项：
    ///   Delay           —— 登录后延迟启动。登录那一瞬间用户配置单元还没挂好、服务子系统也没就绪，
    ///                      这时启动的进程会出现"HKCU 写不了、%APPDATA% 写不了、驱动装不上"，
    ///                      表现出来就是"开机自启后读不到温度、托盘也没图标"。
    ///   DisallowStartIfOnBatteries=false —— 默认值是 true，用电池开机时任务根本不会启动（笔记本常踩）。
    ///   StartWhenAvailable —— 错过了触发点（睡眠/关机）也补跑。
    /// </summary>
    private static string BuildTaskXml(string? targetExe = null)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string exe = targetExe ?? PreferredExe;
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>CPU 玻璃监控：登录后延迟 {StartupDelaySeconds} 秒启动（带管理员权限，不弹 UAC）</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
      <Delay>PT{StartupDelaySeconds}S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>--autostart</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    /// <summary>把计划任务定义导出成字符串（诊断/排查用）。</summary>
    public static string ExportTaskXml() => BuildTaskXml();

    /// <summary>延迟多少秒启动。可通过环境变量 CPUGLASS_AUTOSTART_DELAY 覆盖（调试用）。</summary>
    private static int StartupDelaySeconds
    {
        get
        {
            string raw = Environment.GetEnvironmentVariable("CPUGLASS_AUTOSTART_DELAY") ?? "";
            return int.TryParse(raw, out int v) && v >= 5 && v <= 600 ? v : 45;
        }
    }

    private static bool TryCreateTask(out string error, string? targetExe = null)
    {
        error = "";
        string xmlPath = Path.Combine(Path.GetTempPath(), "CpuGlassMonitor-task.xml");
        try
        {
            // schtasks /XML 要求 UTF-16
            File.WriteAllText(xmlPath, BuildTaskXml(targetExe), System.Text.Encoding.Unicode);

            (int code, string output) = Run("schtasks.exe", new[]
            {
                "/Create", "/TN", TaskName, "/XML", xmlPath, "/F"
            });

            if (code == 0)
            {
                SettingsStore.Log($"开机自启：计划任务已更新（登录后延迟 {StartupDelaySeconds} 秒，最高权限，电池也启动）。");
                return true;
            }

            error = output.Trim();
            SettingsStore.Log("开机自启：计划任务 XML 方式创建失败，回退到命令行方式: " + error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }

        // 回退：老的命令行方式（没有 Delay，但至少能自启）
        string command = $"\"{targetExe ?? PreferredExe}\" --autostart";
        (int code2, string output2) = Run("schtasks.exe", new[]
        {
            "/Create", "/TN", TaskName, "/TR", command, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"
        });
        error = code2 == 0 ? "" : output2.Trim();
        return code2 == 0;
    }

    /// <summary>提权创建计划任务（当前不是管理员时的兜底，会弹一次 UAC）。</summary>
    private static bool TryCreateTaskElevated(out string error)
    {
        error = "";
        string xmlPath = Path.Combine(Path.GetTempPath(), "CpuGlassMonitor-task-elevated.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(), System.Text.Encoding.Unicode);

            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F"
            };

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                error = "无法启动提权进程。";
                return false;
            }

            p.WaitForExit(20000);
            if (!p.HasExited || p.ExitCode != 0)
            {
                error = "提权创建计划任务失败（用户可能取消了 UAC）。";
                return false;
            }

            return TaskExists();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static bool TryDeleteTask(out string error)    {
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
            key.SetValue(RunValue, $"\"{PreferredExe}\" --autostart");
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
