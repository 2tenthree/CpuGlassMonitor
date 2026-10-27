using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CpuGlassMonitor.Core;
using CpuGlassMonitor.Sensors;

namespace CpuGlassMonitor.Ui;

public partial class SettingsWindow : Window
{
    private readonly SensorEngine _engine;
    private readonly AppSettings _settings;
    private readonly AppSettings _original;
    private readonly ObservableCollection<MetricOption> _options = new();
    private readonly List<RadioButton> _intervalButtons = new();
    private readonly List<RadioButton> _layoutButtons = new();
    private readonly List<RadioButton> _swatches = new();
    private ICollectionView? _view;
    private bool _loading = true;
    private string _accent;

    private static readonly string[] AccentChoices =
    {
        "#6FD3FF", "#8BE28B", "#FFC857", "#FF8AA3", "#C9A2FF", "#FFFFFF"
    };

    private static readonly int[] Intervals = { 500, 1000, 2000, 3000 };

    public SettingsWindow(SensorEngine engine, AppSettings settings, bool elevated)
    {
        InitializeComponent();

        _engine = engine;
        _settings = settings;
        _original = settings.Clone();
        _accent = settings.Accent;

        AdminBadge.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        BuildOptions();
        BuildAccentSwatches();
        BuildIntervalButtons();
        BuildLayoutButtons();
        LoadFromSettings();

        _loading = false;
        UpdateCount();
    }

    public AppSettings? Result { get; private set; }

    // ------------------------------------------------------------------ 构建

    private void BuildOptions()
    {
        var selected = new HashSet<string>(_settings.SelectedKeys, StringComparer.Ordinal);

        int index = 0;
        foreach (MetricSpec spec in _engine.Catalog.Values)
        {
            _options.Add(new MetricOption(spec, index++)
            {
                IsSelected = selected.Contains(spec.Key),
                SelectionChanged = OnOptionSelectionChanged
            });
        }

        _view = CollectionViewSource.GetDefaultView(_options);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MetricOption.GroupTitle)));
        _view.SortDescriptions.Add(new SortDescription(nameof(MetricOption.Order), ListSortDirection.Ascending));
        _view.Filter = FilterMetric;
        MetricListBox.ItemsSource = _view;
    }

    private void BuildAccentSwatches()
    {
        foreach (string hex in AccentChoices)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var radio = new RadioButton
            {
                Style = (Style)FindResource("SwatchRadio"),
                Background = brush,
                Tag = hex,
                GroupName = "Accent",
                Margin = new Thickness(0, 0, 8, 0)
            };
            radio.Checked += (_, _) =>
            {
                _accent = (string)radio.Tag;
                if (!_loading) Preview();
            };
            _swatches.Add(radio);
            AccentPanel.Children.Add(radio);
        }
    }

    private void BuildIntervalButtons()
    {
        foreach (int ms in Intervals)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("SegmentRadio"),
                Content = ms >= 1000 ? $"{ms / 1000.0:0.#}s" : $"{ms}ms",
                Tag = ms,
                GroupName = "Interval"
            };
            radio.Checked += (_, _) =>
            {
                if (!_loading) Preview();
            };
            _intervalButtons.Add(radio);
            IntervalPanel.Children.Add(radio);
        }
    }

    private void BuildLayoutButtons()
    {
        foreach ((string text, bool horizontal) in new[] { ("竖排 · 多行卡片", false), ("横排 · 单行长条", true) })
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("SegmentRadio"),
                Content = text,
                Tag = horizontal,
                GroupName = "Layout"
            };
            radio.Checked += (_, _) =>
            {
                if (!_loading) Preview();
            };
            _layoutButtons.Add(radio);
            LayoutPanel.Children.Add(radio);
        }
    }

    private void LoadFromSettings()
    {
        TintSlider.Value = _settings.TintStrength;
        OverlaySlider.Value = _settings.OverlayStrength;
        OpacitySlider.Value = Math.Clamp(_settings.PanelTransparency * 100.0, 0, 92);
        FontSlider.Value = _settings.FontScale;
        ChkSpark.IsChecked = _settings.ShowSparkline;
        ChkColorize.IsChecked = _settings.Colorize;
        ChkTopmost.IsChecked = _settings.AlwaysOnTop;
        ChkLock.IsChecked = _settings.Locked;
        ChkClickThrough.IsChecked = _settings.ClickThrough;
        // 以实际注册情况为准：任务/Run 键还在就显示为勾选
        ChkAutoStart.IsChecked = AutoStart.IsRegistered;
        ChkElevate.IsChecked = _settings.AskElevation;
        ChkShowAll.IsChecked = _settings.ShowAllSensors;

        RadioButton? accent = _swatches.FirstOrDefault(s => (string)s.Tag == _accent, _swatches[0]);
        accent!.IsChecked = true;

        RadioButton interval = _intervalButtons.FirstOrDefault(b => (int)b.Tag == _settings.RefreshMs)
                               ?? _intervalButtons[1];
        interval.IsChecked = true;

        foreach (RadioButton button in _layoutButtons)
        {
            button.IsChecked = (bool)button.Tag == _settings.Horizontal;
        }

        UpdateSliderTexts();
    }

    private void UpdateSliderTexts()
    {
        // InitializeComponent 期间 Slider 的 Minimum/Value 赋值会提前触发 ValueChanged，
        // 此时后面声明的 TextBlock 字段还没创建，必须逐项判空。
        if (TintValueText != null) TintValueText.Text = $"{(int)Math.Round(TintSlider.Value * 100)}%";
        if (OverlayValueText != null) OverlayValueText.Text = $"{(int)Math.Round(OverlaySlider.Value * 100)}%";
        if (OpacityValueText != null) OpacityValueText.Text = $"{(int)Math.Round(OpacitySlider.Value)}%";
        if (FontValueText != null) FontValueText.Text = $"{FontSlider.Value:F2}×";
    }

    private int SelectedInterval()
    {
        RadioButton? checkedButton = _intervalButtons.FirstOrDefault(b => b.IsChecked == true);
        return checkedButton != null ? (int)checkedButton.Tag : 1000;
    }

    private AppSettings BuildSettings()
    {
        AppSettings s = _settings;
        s.TintStrength = TintSlider.Value;
        s.OverlayStrength = OverlaySlider.Value;
        s.PanelTransparency = Math.Clamp(OpacitySlider.Value / 100.0, 0.0, 0.92);
        s.Horizontal = _layoutButtons.Any(b => b.IsChecked == true && b.Tag is true);
        s.FontScale = FontSlider.Value;
        s.ShowSparkline = ChkSpark.IsChecked == true;
        s.Colorize = ChkColorize.IsChecked == true;
        s.AlwaysOnTop = ChkTopmost.IsChecked == true;
        s.Locked = ChkLock.IsChecked == true;
        s.ClickThrough = ChkClickThrough.IsChecked == true;
        s.WithWindows = ChkAutoStart.IsChecked == true;
        s.AskElevation = ChkElevate.IsChecked == true;
        s.ShowAllSensors = ChkShowAll.IsChecked == true;
        s.Accent = _accent;
        s.RefreshMs = SelectedInterval();
        s.SelectedKeys = _options
            .Where(o => o.IsSelected)
            .OrderBy(o => o.Order)
            .Select(o => o.Key)
            .ToList();
        s.DefaultsApplied = true;
        return s;
    }

    // ------------------------------------------------------------------ 交互

    private void Preview()
    {
        if (_loading) return;
        AppSettings preview = BuildSettings();
        if (Application.Current is App app)
        {
            app.PreviewSettings(preview);
        }
    }

    private bool FilterMetric(object item)
    {
        if (item is not MetricOption option) return false;

        if (option.IsAdvanced && ChkShowAll.IsChecked != true) return false;

        string query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;

        return option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
               || option.GroupTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
               || option.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnOptionSelectionChanged()
    {
        UpdateCount();
        Preview();
    }

    private void UpdateCount()
    {
        int count = _options.Count(o => o.IsSelected);
        SelectedCount.Text = $"已选 {count} 项";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
    }

    private void ShowAll_Changed(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        Preview();
    }

    private void TintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TintValueText == null) return;
        UpdateSliderTexts();
        Preview();
    }

    private void OverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayValueText == null) return;
        UpdateSliderTexts();
        Preview();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText == null) return;
        UpdateSliderTexts();
        Preview();
    }

    private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontValueText == null) return;
        UpdateSliderTexts();
        Preview();
    }

    private void ApplyRecommended_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new HashSet<string>(
            _engine.Catalog.Values.Where(s => s.DefaultOn).Select(s => s.Key), StringComparer.Ordinal);
        foreach (MetricOption option in _options)
        {
            option.IsSelected = defaults.Contains(option.Key);
        }

        UpdateCount();
        Preview();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (MetricOption option in _options) option.IsSelected = false;
        UpdateCount();
        Preview();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        TintSlider.Value = defaults.TintStrength;
        OverlaySlider.Value = defaults.OverlayStrength;
        OpacitySlider.Value = Math.Clamp(defaults.PanelTransparency * 100.0, 0, 92);
        FontSlider.Value = defaults.FontScale;
        ChkSpark.IsChecked = defaults.ShowSparkline;
        ChkColorize.IsChecked = defaults.Colorize;
        ChkTopmost.IsChecked = defaults.AlwaysOnTop;
        ChkLock.IsChecked = defaults.Locked;
        ChkClickThrough.IsChecked = defaults.ClickThrough;
        ChkAutoStart.IsChecked = AutoStart.IsRegistered;
        ChkElevate.IsChecked = defaults.AskElevation;
        ChkShowAll.IsChecked = false;

        _accent = defaults.Accent;
        foreach (RadioButton swatch in _swatches) swatch.IsChecked = (string)swatch.Tag == _accent;
        foreach (RadioButton button in _intervalButtons) button.IsChecked = (int)button.Tag == defaults.RefreshMs;
        foreach (RadioButton button in _layoutButtons) button.IsChecked = (bool)button.Tag == defaults.Horizontal;

        ApplyRecommended_Click(sender, e);
        UpdateSliderTexts();
    }

    /// <summary>把右侧选项区滚到底（仅供截图/自动化验证使用）。</summary>
    public void ScrollOptionsToEnd()
    {
        OptionsScroll.ScrollToEnd();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettings built = BuildSettings();

        // 开机自启是系统级状态，不能只写配置文件：按实际注册情况对齐一次
        bool want = built.WithWindows;
        if (want != AutoStart.IsRegistered)
        {
            if (!AutoStart.Sync(want, out string message))
            {
                MessageBox.Show(this, message, "开机自启", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 以系统真实状态回写，避免"取消勾选后重开又变成勾上"
        built.WithWindows = AutoStart.IsRegistered;
        ChkAutoStart.IsChecked = built.WithWindows;

        Result = built;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Cancel();
    }

    private void Cancel()
    {
        if (Application.Current is App app)
        {
            app.PreviewSettings(_original);
        }

        DialogResult = false;
        Close();
    }
}
