using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CpuGlassMonitor.Core;
using CpuGlassMonitor.Interop;
using CpuGlassMonitor.Sensors;

namespace CpuGlassMonitor.Ui;

public partial class MainWindow : Window
{
    private readonly SensorEngine _engine;
    private readonly bool _elevated;
    private SensorReading? _lastReading;
    private readonly ObservableCollection<MetricRow> _rows = new();
    private readonly DispatcherTimer _savePositionTimer;
    private AppSettings _settings;
    private IntPtr _hwnd;
    private GlassMode _glassMode = GlassMode.Solid;
    private GlassMode _loggedGlassMode = (GlassMode)(-1);
    private bool _engineReady;
    private bool _updateQueued;
    private bool _restoringPosition;
    private SettingsWindow? _settingsWindow;

    private static readonly Brush PrimaryValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF7, 0xFF)));
    private static readonly Brush WarnValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)));
    private static readonly Brush CritValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6E, 0x6E)));

    /// <summary>透明面板上的文字描边阴影。面板越透，阴影越重，浅色字才压得住亮背景。</summary>
    private static DropShadowEffect CreateTextShadow(double transparency)
    {
        var effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 7,
            ShadowDepth = 0,
            Direction = 0,
            Opacity = Math.Clamp(0.45 + (transparency * 0.45), 0, 0.95)
        };
        effect.Freeze();
        return effect;
    }

    public MainWindow(SensorEngine engine, AppSettings settings, bool elevated)
    {
        InitializeComponent();

        _engine = engine;
        _settings = settings;
        _elevated = elevated;

        MetricList.ItemsSource = _rows;
        MetricListH.ItemsSource = _rows;

        _savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _savePositionTimer.Tick += (_, _) =>
        {
            _savePositionTimer.Stop();
            PersistWindowState();
        };

        Root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
        LocationChanged += (_, _) => QueuePersistWindowState();
        SizeChanged += (_, _) => QueuePersistWindowState();

        _engine.Updated += OnReading;
        ApplySettings(_settings, rebuildRows: false);
    }

    // ------------------------------------------------------------------ 生命周期

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        GlassHelper.HideFromAltTab(_hwnd);
        GlassHelper.ExtendGlassFrame(_hwnd);
        RestorePosition();
        ApplyGlass();
        ApplyClickThrough();
    }

    public void OnEngineReady()
    {
        _engineReady = true;
        ToolTip = _engine.CpuName + (string.IsNullOrEmpty(_engine.GpuName) ? "" : "  ·  " + _engine.GpuName);
        RebuildRows();
        UpdateHint();
        ApplyGlass();

        // 排查 CPU 温度读不到时全靠这几行日志
        SettingsStore.Log($"就绪 elevated={_elevated} cpuTemp={_engine.CpuTemperatureAvailable} " +
                          $"catalog={_engine.Catalog.Count} rows={_rows.Count} cpu={_engine.CpuName}");
    }

    /// <summary>调试用：以窗口为锚点打开右键菜单，便于无桌面环境截图校验菜单样式。</summary>
    public void OpenContextMenuForTest()
    {
        Menu.PlacementTarget = this;
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
        Menu.HorizontalOffset = 12;
        Menu.VerticalOffset = 30;
        Menu.IsOpen = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Updated -= OnReading;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ 数据更新

    private void OnReading(SensorReading reading)
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _updateQueued = false;
            _lastReading = reading;
            foreach (MetricRow row in _rows) row.Update(reading, _settings.Colorize);
            // 提示语依赖真实读数（温度要等驱动就绪才有），每帧刷新一次，读到就能自动消失
            UpdateHint();
        }));
    }

    // ------------------------------------------------------------------ 设置应用

    public void ApplySettings(AppSettings settings, bool rebuildRows = true)
    {
        _settings = settings;
        Topmost = settings.AlwaysOnTop;

        ApplyAccent(settings.Accent);
        ApplyScaleResources();
        ApplyLayout();
        ApplyGlass();
        ApplyClickThrough();

        MenuTopmost.IsChecked = settings.AlwaysOnTop;
        MenuLocked.IsChecked = settings.Locked;
        MenuClickThrough.IsChecked = settings.ClickThrough;
        MenuSpark.IsChecked = settings.ShowSparkline;
        MenuHorizontal.IsChecked = settings.Horizontal;
        MenuAutoStart.IsChecked = settings.WithWindows;

        _engine.IntervalMs = settings.RefreshMs;

        if (rebuildRows && _engineReady) RebuildRows();
        UpdateHint();

        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampToScreen));
        }
    }

    /// <summary>竖排（多行卡片）与横排（单行长条）之间切换窗口的自适应方式。</summary>
    private void ApplyLayout()
    {
        bool horizontal = _settings.Horizontal;

        MetricList.Visibility = horizontal ? Visibility.Collapsed : Visibility.Visible;
        MetricListH.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;

        if (horizontal)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 0;
            // 选了很多参数时单行会很长，别让它超出屏幕可用宽度
            Rect work = PrimaryWorkArea();
            MaxWidth = Math.Min(1400, Math.Max(320, work.Width - 40));
            Width = double.NaN;
            CardInner.Margin = new Thickness(13, 9, 13, 10);
            HintText.MaxWidth = 460;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            MinWidth = 236;
            MaxWidth = 640;
            CardInner.Margin = new Thickness(15, 11, 14, 12);
            HintText.MaxWidth = double.PositiveInfinity;
            if (_settings.WindowWidth >= 236 && _settings.WindowWidth <= 640)
            {
                Width = _settings.WindowWidth;
            }
        }
    }

    /// <summary>切换排布后窗口尺寸会变，若越过屏幕右/下边缘就拉回来。</summary>
    private void ClampToScreen()
    {
        Rect work = PrimaryWorkArea();
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;

        if (!double.IsNaN(w) && Left + w > work.Right - 8)
        {
            Left = Math.Max(work.Left + 8, work.Right - w - 16);
        }

        if (!double.IsNaN(h) && Top + h > work.Bottom - 8)
        {
            Top = Math.Max(work.Top + 8, work.Bottom - h - 16);
        }
    }

    private void ApplyScaleResources()
    {
        double s = Math.Clamp(_settings.FontScale, 0.75, 1.8);
        Resources["LabelSize"] = 11.5 * s;
        Resources["ValueSize"] = 16.0 * s;
        Resources["UnitSize"] = 9.5 * s;
        Resources["FooterSize"] = 9.5 * s;
        Resources["RowHeight"] = 22.0 * s;
        Resources["ValueColumnWidth"] = new GridLength(48.0 * s);
        Resources["UnitColumnWidth"] = new GridLength(24.0 * s);
        Resources["SparkWidth"] = _settings.ShowSparkline ? 46.0 * s : 0.0;
        Resources["SparkWidthH"] = _settings.ShowSparkline ? 30.0 * s : 0.0;
        Resources["SparkHeight"] = 13.0 * s;
    }

    private void ApplyAccent(string hex)
    {
        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            color = Color.FromRgb(0x6F, 0xD3, 0xFF);
        }

        Application.Current.Resources["AccentColor"] = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources["AccentBrush"] = brush;
        Application.Current.Resources["AccentDimColor"] = Color.FromArgb(0x3A, color.R, color.G, color.B);
    }

    private void ApplyGlass()
    {
        if (_hwnd == IntPtr.Zero) return;

        // 浓度 = 玻璃底色深浅（越高越暗），透明度 = 能看穿多少
        double concentration = Math.Clamp(_settings.TintStrength, 0.0, 0.98);
        double transparency = Math.Clamp(_settings.PanelTransparency, 0.0, 0.92);

        // 面板蒙版的不透明度：透明度越大，蒙版越淡，背后的桌面/游戏就越清楚
        double maskAlpha = 0.94 * (1.0 - transparency);
        Color tint = Mix(Color.FromRgb(0x28, 0x32, 0x40), Color.FromRgb(0x04, 0x06, 0x09), concentration);

        _glassMode = GlassHelper.ApplyLayered(_hwnd, tint, maskAlpha);

        // 亚克力模糊自己会画一层带 alpha 的底色，XAML 这边只补一点点；
        // 没能用上亚克力时（分层窗口 / 系统不支持），全部靠这层蒙版表现透明度。
        double scrimAlpha = _glassMode switch
        {
            GlassMode.Acrylic => maskAlpha * 0.30,
            _ => maskAlpha
        };
        var scrim = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(Math.Round(scrimAlpha * 255), 0, 255), tint.R, tint.G, tint.B));
        scrim.Freeze();
        Scrim.Background = scrim;

        // 高光是一层白色渐变；面板越透明，它越容易把"透明"洗成一块灰雾，所以跟着减弱
        double overlay = Math.Clamp(_settings.OverlayStrength, 0.0, 1.0) * (1.0 - (transparency * 0.55));        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.35, 1)
        };
        byte top = (byte)Math.Clamp(overlay * 30, 0, 255);
        byte bottom = (byte)Math.Clamp(overlay * 7, 0, 255);
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(top, 255, 255, 255), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(bottom, 255, 255, 255), 1));
        gradient.Freeze();
        Card.Background = gradient;

        // 面板变透明后，浅色文字压在亮背景上会糊掉，给文字加一层很淡的黑色描边阴影
        var shadow = transparency >= 0.25 ? CreateTextShadow(transparency) : null;
        MetricList.Effect = shadow;
        MetricListH.Effect = shadow;
        HintText.Effect = shadow;

        if (_loggedGlassMode != _glassMode)
        {
            _loggedGlassMode = _glassMode;
            SettingsStore.Log($"玻璃模式={_glassMode} 浓度={concentration:F2} 透明度={transparency:F2} 蒙版={maskAlpha:F3} 高光={overlay:F2}");
        }
    }

    /// <summary>两个颜色按比例线性插值，t=0 取 a，t=1 取 b。</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        double k = Math.Clamp(t, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(a.R + ((b.R - a.R) * k)),
            (byte)Math.Round(a.G + ((b.G - a.G) * k)),
            (byte)Math.Round(a.B + ((b.B - a.B) * k)));
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        GlassHelper.SetClickThrough(_hwnd, _settings.ClickThrough);
    }

    private void RebuildRows()
    {
        _rows.Clear();
        var catalog = _engine.Catalog;
        if (catalog.Count == 0)
        {
            UpdateHint();
            return;
        }

        foreach (string key in _settings.SelectedKeys)
        {
            if (!catalog.TryGetValue(key, out MetricSpec? spec)) continue;
            _rows.Add(new MetricRow(spec, PrimaryValueBrush, WarnValueBrush, CritValueBrush)
            {
                ShowSpark = _settings.ShowSparkline
            });
        }

        UpdateHint();
    }

    private void UpdateHint()
    {
        string? hint = null;
        if (!_engineReady)
        {
            hint = "正在读取传感器…";
        }
        else if (!string.IsNullOrEmpty(_engine.Error))
        {
            hint = _engine.Error;
        }
        else if (_rows.Count == 0)
        {
            hint = "还没有选择要显示的参数，双击面板打开设置";
        }
        else if (WantsCpuTemperature() && !HasCpuTemperatureNow())
        {
            hint = _elevated || _engine.CpuTemperatureAvailable
                ? "未读到 CPU 温度：这台机器的温控芯片可能还没被支持，可在设置里改看负载/频率"
                : "受限模式：CPU 温度/频率/电压需要管理员权限";
        }

        HintText.Text = hint ?? "";
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>当前选中的参数里有没有 CPU 温度（聚合项或原始项）。</summary>
    private bool WantsCpuTemperature()
    {
        foreach (MetricRow row in _rows)
        {
            if (row.Key is AggKeys.CpuTempMax or AggKeys.CpuTempAvg) return true;
            if (row.Spec.Group == "CPU" && row.Spec.Unit == "°C") return true;
        }

        return false;
    }

    /// <summary>这一帧是否真的读到了 CPU 温度。</summary>
    private bool HasCpuTemperatureNow()
    {
        SensorReading? reading = _lastReading;
        if (reading is null) return false;
        return reading.Get(AggKeys.CpuTempMax).HasValue || reading.Get(AggKeys.CpuTempAvg).HasValue;
    }

    // ------------------------------------------------------------------ 窗口位置

    private double DpiScaleX => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    private double DpiScaleY => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

    private void RestorePosition()
    {
        _restoringPosition = true;
        try
        {
            Rect work = PrimaryWorkArea();
            double width = ActualWidth > 0 ? ActualWidth : Width;

            if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
            {
                var saved = new Rect(_settings.WindowLeft.Value, _settings.WindowTop.Value, width, 140);
                if (IsOnSomeScreen(saved))
                {
                    Left = saved.X;
                    Top = saved.Y;
                    return;
                }
            }

            Left = work.Right - width - 30;
            Top = work.Top + 30;
        }
        finally
        {
            _restoringPosition = false;
        }
    }

    private Rect PrimaryWorkArea()
    {
        System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double sx = DpiScaleX;
        double sy = DpiScaleY;
        return new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
    }

    private bool IsOnSomeScreen(Rect rect)
    {
        double sx = DpiScaleX;
        double sy = DpiScaleY;
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            System.Drawing.Rectangle wa = screen.WorkingArea;
            var area = new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
            if (area.IntersectsWith(rect)) return true;
        }

        return false;
    }

    private void QueuePersistWindowState()
    {
        if (_restoringPosition) return;
        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void PersistWindowState()
    {
        if (_restoringPosition) return;
        if (WindowState != WindowState.Normal) return;

        AppSettings settings = ((App)Application.Current).Settings;
        settings.WindowLeft = Left;
        settings.WindowTop = Top;
        if (!settings.Horizontal)
        {
            settings.WindowWidth = ActualWidth;
        }

        SettingsStore.Save(settings);
    }

    // ------------------------------------------------------------------ 交互

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            OpenSettings_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (_settings.Locked || _settings.ClickThrough) return;

        try
        {
            DragMove();
        }
        catch
        {
            // 鼠标状态异常时忽略
        }

        QueuePersistWindowState();
    }

    public void OpenSettingsFromTray() => OpenSettings_Click(this, new RoutedEventArgs());

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        if (!_engineReady)
        {
            HintText.Text = "传感器还在初始化，请稍候…";
            HintText.Visibility = Visibility.Visible;
            return;
        }

        _settingsWindow = new SettingsWindow(_engine, _settings.Clone(), _elevated)
        {
            Owner = this,
            Topmost = Topmost
        };

        bool? ok = _settingsWindow.ShowDialog();
        if (ok == true && _settingsWindow.Result != null)
        {
            ((App)Application.Current).ApplySettings(_settingsWindow.Result);
        }

        _settingsWindow = null;
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void Exit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApp();

    private void MenuTopmost_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ((App)Application.Current).Settings;
        settings.AlwaysOnTop = MenuTopmost.IsChecked;
        ((App)Application.Current).ApplySettings(settings);
    }

    private void MenuLocked_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ((App)Application.Current).Settings;
        settings.Locked = MenuLocked.IsChecked;
        ((App)Application.Current).ApplySettings(settings);
    }

    private void MenuClickThrough_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ((App)Application.Current).Settings;
        settings.ClickThrough = MenuClickThrough.IsChecked;
        ((App)Application.Current).ApplySettings(settings);
    }

    private void MenuSpark_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ((App)Application.Current).Settings;
        settings.ShowSparkline = MenuSpark.IsChecked;
        ((App)Application.Current).ApplySettings(settings);
    }

    private void MenuHorizontal_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ((App)Application.Current).Settings;
        settings.Horizontal = MenuHorizontal.IsChecked;
        ((App)Application.Current).ApplySettings(settings);
    }

    private void MenuAutoStart_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ((App)Application.Current).Settings;
        bool target = MenuAutoStart.IsChecked;
        string message;
        bool ok = target ? AutoStart.Enable(out message) : AutoStart.Disable(out message);
        settings.WithWindows = ok && target;
        ((App)Application.Current).ApplySettings(settings);
        MenuAutoStart.IsChecked = settings.WithWindows;
        MessageBox.Show(this, message, "开机自启", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowAndActivate();
        }
    }

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = _settings.AlwaysOnTop;
    }

    // ------------------------------------------------------------------ 工具

    private static string ShortCpuName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "CPU 监控";
        string n = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        int idx = n.IndexOf("Core ", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) n = n[(idx + 5)..];
        idx = n.IndexOf(" CPU", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) n = n[..idx];
        n = n.Trim();
        return string.IsNullOrWhiteSpace(n) ? "CPU 监控" : n;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public GlassMode CurrentGlassMode => _glassMode;
}
