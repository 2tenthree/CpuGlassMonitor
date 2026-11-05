using System.Windows.Media;

namespace CpuGlassMonitor.Ui;

/// <summary>
/// 分组配色。面板标签改成图标以后，同形状的图标（比如两个温度计）靠颜色区分钟是 CPU 还是显卡。
/// </summary>
public static class GroupPalette
{
    public static Brush For(string group)
    {
        Color color = group switch
        {
            "CPU" => Color.FromRgb(0x7F, 0xD9, 0xFF),
            "显卡" => Color.FromRgb(0x9F, 0xE8, 0xA8),
            "内存" => Color.FromRgb(0xFF, 0xC9, 0x80),
            "网络" => Color.FromRgb(0xC9, 0xA2, 0xFF),
            "主板" => Color.FromRgb(0xFF, 0xB4, 0xA2),
            "硬盘" => Color.FromRgb(0xA8, 0xD8, 0xFF),
            "风扇" => Color.FromRgb(0xC7, 0xE0, 0xF0),
            _ => Color.FromRgb(0xAE, 0xBF, 0xCF)
        };

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
