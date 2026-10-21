using System.ComponentModel;
using System.Windows.Media;
using CpuGlassMonitor.Sensors;

namespace CpuGlassMonitor.Ui;

/// <summary>悬浮窗里的一行指标视图模型。</summary>
public sealed class MetricRow : INotifyPropertyChanged
{
    private const int Capacity = 72;

    private readonly List<double> _history = new(Capacity + 1);
    private double[] _historyView = Array.Empty<double>();
    private string _valueText = "--";
    private Brush _valueBrush;
    private bool _hasValue;

    public MetricRow(MetricSpec spec, Brush primary, Brush warn, Brush crit)
    {
        Spec = spec;
        _valueBrush = primary;
        PrimaryBrush = primary;
        WarnBrush = warn;
        CritBrush = crit;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MetricSpec Spec { get; }

    public string Key => Spec.Key;

    public string Label => Spec.Label;

    public string Unit => Spec.Unit;

    public Brush PrimaryBrush { get; }

    public Brush WarnBrush { get; }

    public Brush CritBrush { get; }

    public bool HasValue
    {
        get => _hasValue;
        private set
        {
            if (_hasValue == value) return;
            _hasValue = value;
            OnChanged(nameof(HasValue));
        }
    }

    public string ValueText
    {
        get => _valueText;
        private set
        {
            if (_valueText == value) return;
            _valueText = value;
            OnChanged(nameof(ValueText));
        }
    }

    public Brush ValueBrush
    {
        get => _valueBrush;
        private set
        {
            if (ReferenceEquals(_valueBrush, value)) return;
            _valueBrush = value;
            OnChanged(nameof(ValueBrush));
        }
    }

    private bool _showSpark = true;

    public bool ShowSpark
    {
        get => _showSpark;
        set
        {
            if (_showSpark == value) return;
            _showSpark = value;
            OnChanged(nameof(ShowSpark));
        }
    }

    /// <summary>走势曲线用的快照：每次更新都换一个新的数组实例，否则绑定拿到的引用没变，曲线不会重绘。</summary>
    public IReadOnlyList<double> History => _historyView;

    public double? SparkMin => Spec.SparkMin;

    public double? SparkMax => Spec.SparkMax;

    public void Update(SensorReading reading, bool colorize)
    {
        double? raw = reading.Get(Key);
        if (raw is null)
        {
            HasValue = false;
            ValueText = "--";
            ValueBrush = PrimaryBrush;
            return;
        }

        HasValue = true;
        ValueText = Spec.Format(raw.Value);

        double shown = raw.Value * Spec.Scale;
        Brush brush = PrimaryBrush;
        if (colorize)
        {
            if (Spec.Crit.HasValue && shown >= Spec.Crit.Value) brush = CritBrush;
            else if (Spec.Warn.HasValue && shown >= Spec.Warn.Value) brush = WarnBrush;
        }

        if (!ReferenceEquals(brush, ValueBrush)) ValueBrush = brush;

        _history.Add(shown);
        while (_history.Count > Capacity) _history.RemoveAt(0);
        _historyView = _history.ToArray();
        OnChanged(nameof(History));
    }

    /// <summary>指标消失（例如设备拔出）时清空曲线。</summary>
    public void Reset()
    {
        _history.Clear();
        _historyView = Array.Empty<double>();
        OnChanged(nameof(History));
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
