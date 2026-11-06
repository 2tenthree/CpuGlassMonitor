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

    public static string LogPath => Path.Combine(Directory, "app.log");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (s != null)
                {
                    s.RefreshMs = Math.Clamp(s.RefreshMs, 250, 10000);
                    s.FontScale = Math.Clamp(s.FontScale, 0.75, 1.8);
                    s.TintStrength = Math.Clamp(s.TintStrength, 0.0, 0.98);
                    s.OverlayStrength = Math.Clamp(s.OverlayStrength, 0.0, 1.0);
                    s.PanelTransparency = Math.Clamp(s.PanelTransparency, 0.0, 0.92);
                    if (string.IsNullOrWhiteSpace(s.Accent)) s.Accent = "#6FD3FF";
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log("读取设置失败: " + ex);
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("保存设置失败: " + ex);
        }
    }

    public static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
