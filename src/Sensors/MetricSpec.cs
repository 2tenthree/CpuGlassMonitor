namespace CpuGlassMonitor.Sensors;

/// <summary>一个可显示/可勾选的指标（既可以是原始传感器，也可以是本程序计算的聚合值）。</summary>
public sealed class MetricSpec
{
    public required string Key { get; init; }

    /// <summary>设置面板里的分组，如 CPU / 显卡 / 内存。</summary>
    public required string Group { get; init; }

    /// <summary>第二级分组，如 温度 / 频率 / 电压。</summary>
    public required string SubGroup { get; init; }

    /// <summary>显示名称。</summary>
    public required string Label { get; init; }

    public string Unit { get; init; } = "";

    public double Scale { get; init; } = 1.0;

    public int Digits { get; init; }

    /// <summary>超过该值显示警告色。</summary>
    public double? Warn { get; init; }

    /// <summary>超过该值显示危险色。</summary>
    public double? Crit { get; init; }

    public bool DefaultOn { get; init; }

    /// <summary>杂项传感器：默认不在列表里出现（除非用户打开“显示全部”）。</summary>
    public bool Advanced { get; init; }

    /// <summary>用于曲线归一化的固定范围（null 表示按历史自动缩放）。</summary>
    public double? SparkMin { get; init; }

    public double? SparkMax { get; init; }

    public string Format(double raw)
    {
        double v = raw * Scale;
        return v.ToString("F" + Digits, System.Globalization.CultureInfo.InvariantCulture);
    }
}
