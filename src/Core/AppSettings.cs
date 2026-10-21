using System.Text.Json;

namespace CpuGlassMonitor.Core;

/// <summary>用户可调的全部设置，持久化到 %APPDATA%\CpuGlassMonitor\settings.json。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>要显示的指标 key 列表（顺序即显示顺序）。</summary>
    public List<string> SelectedKeys { get; set; } = new();

    /// <summary>是否已经写入过默认指标（首次启动后置为 true，避免覆盖用户选择）。</summary>
    public bool DefaultsApplied { get; set; }

    /// <summary>亚克力底色浓度 0..1（越大越不透明）。</summary>
    public double TintStrength { get; set; } = 0.55;

    /// <summary>叠加渐变亮片的透明度 0..1。</summary>
    public double OverlayStrength { get; set; } = 0.35;

    /// <summary>
    /// 面板透明度 0..0.92（越大越透，能直接看穿到背后的桌面 / 游戏）。
    /// 只影响玻璃底色蒙版，文字与曲线始终不透明，保证可读性。
    /// </summary>
    public double PanelTransparency { get; set; } = 0.25;

    /// <summary>横向排列：所有指标挤在一行，做成细长条（如游戏 HUD）。</summary>
    public bool Horizontal { get; set; }

    /// <summary>刷新间隔（毫秒），250..5000。</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>字号缩放 0.8..1.6。</summary>
    public double FontScale { get; set; } = 1.0;

    public bool ShowSparkline { get; set; } = true;

    public bool AlwaysOnTop { get; set; } = true;

    public bool ClickThrough { get; set; }

    public bool Locked { get; set; }

    /// <summary>数值按阈值着色（温度/负载）。</summary>
    public bool Colorize { get; set; } = true;

    /// <summary>启动时请求管理员权限（读取 CPU 温度/频率/电压必需）。</summary>
    public bool AskElevation { get; set; } = true;

    /// <summary>在设置里列出全部传感器（含高级/杂项）。</summary>
    public bool ShowAllSensors { get; set; }

    public string Accent { get; set; } = "#6FD3FF";

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; } = 300;

    public bool WithWindows { get; set; }

    public AppSettings Clone()
    {
        string json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }
}
