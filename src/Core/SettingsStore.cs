using System.IO;
using System.Text.Json;

namespace CpuGlassMonitor.Core;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string? _directory;

    public static string Directory => _directory ??= ResolveDirectory();

    /// <summary>调试用（--config &lt;目录&gt;）：把配置目录改到指定位置，便于提权实例也能用测试配置。</summary>
    public static void OverrideDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)) _directory = directory;
    }

    private static string ResolveDirectory()
    {
        string? custom = Environment.GetEnvironmentVariable("CPUGLASS_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CpuGlassMonitor");
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>%APPDATA% 写不进去时的备用位置，与日志的兜底策略保持一致。</summary>
    public static string FallbackFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static string LogPath => Path.Combine(Directory, "app.log");

    public static AppSettings Load()
    {
        // 上一次可能因为令牌不完整而把配置存到了程序目录，这里两个位置都认。
        foreach (string path in new[] { FilePath, FallbackFilePath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (s == null) continue;

                s.RefreshMs = Math.Clamp(s.RefreshMs, 250, 10000);
                s.FontScale = Math.Clamp(s.FontScale, 0.75, 1.8);
                s.TintStrength = Math.Clamp(s.TintStrength, 0.0, 0.98);
                s.OverlayStrength = Math.Clamp(s.OverlayStrength, 0.0, 1.0);
                s.PanelTransparency = Math.Clamp(s.PanelTransparency, 0.0, 0.92);
                if (string.IsNullOrWhiteSpace(s.Accent)) s.Accent = "#6FD3FF";
                return s;
            }
            catch (Exception ex)
            {
                Log("读取设置失败(" + path + "): " + ex);
            }
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, Options);
        }
        catch (Exception ex)
        {
            Log("序列化设置失败: " + ex);
            return;
        }

        if (TrySave(FilePath, json)) return;

        // 开机自启拿到残缺令牌时 %APPDATA% 会被拒绝访问；退到程序目录，
        // 至少这次的设置不会静默丢失，下次 Load 也能读回来。
        if (TrySave(FallbackFilePath, json))
        {
            Log("设置已保存到程序目录（%APPDATA% 不可写）: " + FallbackFilePath);
            return;
        }

        Log("保存设置失败：%APPDATA% 与程序目录都不可写");
    }

    private static bool TrySave(string path, string json)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

        // 主日志：%APPDATA%\CpuGlassMonitor\app.log
        if (TryAppend(LogPath, line)) return;

        // 兜底 1：%APPDATA% 写不进去（权限/沙箱/被杀软拦），退到程序目录
        if (TryAppend(Path.Combine(AppContext.BaseDirectory, "app.log"), line)) return;

        // 兜底 2：程序目录也写不了，退到临时目录
        TryAppend(Path.Combine(Path.GetTempPath(), "CpuGlassMonitor.log"), line);
    }

    /// <summary>写一行日志；共享 FileShare.ReadWrite，避免另一个实例正持有文件时整段日志丢失。</summary>
    private static bool TryAppend(string path, string line)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(fs, System.Text.Encoding.UTF8);
            writer.Write(line);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
