using System.ComponentModel;
using CpuGlassMonitor.Sensors;

namespace CpuGlassMonitor.Ui;

/// <summary>设置面板中的一个可勾选参数。</summary>
public sealed class MetricOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public MetricOption(MetricSpec spec, int insertionIndex)
    {
        Spec = spec;
        Label = spec.Label;
        GroupTitle = spec.Group;
        Detail = string.IsNullOrEmpty(spec.Unit) ? spec.SubGroup : $"{spec.SubGroup} · {spec.Unit}";
        if (spec.Advanced) Detail += " · 杂项";

        int groupRank = spec.Group switch
        {
            "CPU" => 0,
            "显卡" => 1,
            "内存" => 2,
            "主板" => 3,
            "风扇" => 4,
            "硬盘" => 5,
            "网络" => 6,
            _ => 9
        };

        int subRank = spec.SubGroup switch
        {
            "温度" => 0,
            "频率" => 1,
            "电压" => 2,
            "功耗" => 3,
            "负载" => 4,
            "风扇" => 5,
            "占用" => 6,
            _ => 7
        };

        bool aggregate = spec.Key.StartsWith("agg.", StringComparison.Ordinal);
        Order = groupRank * 1_000_000 + subRank * 10_000 + (aggregate ? 0 : 5_000) + insertionIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MetricSpec Spec { get; }

    public string Key => Spec.Key;

    public string Label { get; }

    public string Detail { get; }

    public string GroupTitle { get; }

    public int Order { get; }

    public bool IsAdvanced => Spec.Advanced;

    public Action? SelectionChanged { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke();
        }
    }
}
