namespace CpuGlassMonitor.Sensors;

/// <summary>
/// 面板图标字形表（Segoe Fluent Icons，Win10 上自动回退到 Segoe MDL2 Assets，两者码位一致）。
/// 用图标代替中文标签，既省横向空间也更像仪表盘。
/// </summary>
public static class Icons
{
    /// <summary>温度：温度计。</summary>
    public const string Temperature = "\uE9CA";

    /// <summary>频率：时钟表盘（时钟频率）。</summary>
    public const string Clock = "\uED5A";

    /// <summary>占用率：折线图。</summary>
    public const string Load = "\uE9D9";

    /// <summary>功耗：闪电。</summary>
    public const string Power = "\uE945";

    /// <summary>电压：滑杆。</summary>
    public const string Voltage = "\uE9E9";

    /// <summary>风扇转速：循环箭头。</summary>
    public const string Fan = "\uE9F3";

    /// <summary>容量 / 吞吐量：内存条。</summary>
    public const string Capacity = "\uE8F1";

    /// <summary>带宽 / 吞吐：折线。</summary>
    public const string Throughput = "\uE9D2";

    /// <summary>下载：向下箭头。</summary>
    public const string Download = "\uE74B";

    /// <summary>上传：向上箭头。</summary>
    public const string Upload = "\uE74A";

    /// <summary>兜底图标。</summary>
    public const string Default = Load;
}
