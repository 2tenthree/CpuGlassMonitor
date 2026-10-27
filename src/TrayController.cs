using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Resources;
using CpuGlassMonitor.Core;
using CpuGlassMonitor.Ui;
using Forms = System.Windows.Forms;

namespace CpuGlassMonitor;

/// <summary>托盘图标与托盘右键菜单。</summary>
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly MainWindow _window;
    private MemoryStream? _iconStream;

    private Forms.ToolStripMenuItem _showItem = null!;
    private Forms.ToolStripMenuItem _settingsItem = null!;
    private Forms.ToolStripMenuItem _topmostItem = null!;
    private Forms.ToolStripMenuItem _lockItem = null!;
    private Forms.ToolStripMenuItem _clickThroughItem = null!;
    private Forms.ToolStripMenuItem _sparkItem = null!;
    private Forms.ToolStripMenuItem _horizontalItem = null!;
    private Forms.ToolStripMenuItem _autoStartItem = null!;

    public TrayController(MainWindow window, AppSettings settings)
    {
        _window = window;

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "CPU 玻璃监控",
            Icon = LoadIcon(),
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window.ToggleVisible();
        };

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
            Renderer = new DarkMenuRenderer(),
            Padding = new Forms.Padding(2)
        };

        _showItem = new Forms.ToolStripMenuItem("显示 / 隐藏面板", null, (_, _) => _window.ToggleVisible());
        _settingsItem = new Forms.ToolStripMenuItem("显示设置…", null, (_, _) =>
        {
            _window.ShowAndActivate();
            _window.OpenSettingsFromTray();
        });

        _topmostItem = CreateCheck("窗口置顶", () => App.CurrentSettings.AlwaysOnTop, v => App.CurrentSettings.AlwaysOnTop = v);
        _lockItem = CreateCheck("锁定位置", () => App.CurrentSettings.Locked, v => App.CurrentSettings.Locked = v);
        _clickThroughItem = CreateCheck("鼠标穿透", () => App.CurrentSettings.ClickThrough, v => App.CurrentSettings.ClickThrough = v);
        _sparkItem = CreateCheck("走势曲线", () => App.CurrentSettings.ShowSparkline, v => App.CurrentSettings.ShowSparkline = v);
        _horizontalItem = CreateCheck("横向排列", () => App.CurrentSettings.Horizontal, v => App.CurrentSettings.Horizontal = v);
        _autoStartItem = new Forms.ToolStripMenuItem("开机自启", null, (_, _) =>
        {
            // 目标状态取反"系统真实状态"，而不是配置文件里的标记
            bool target = !AutoStart.IsRegistered;
            bool ok = AutoStart.Sync(target, out string message);
            if (!ok && target)
            {
                message += "\n\n提示：设置开机自启需要管理员权限。";
            }

            App.CurrentSettings.WithWindows = AutoStart.IsRegistered;
            App.NotifySettingsChanged();
            _autoStartItem.Checked = AutoStart.IsRegistered;
            Forms.MessageBox.Show(message, "开机自启", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
        });

        _menu.Items.Add(_showItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_topmostItem);
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(_clickThroughItem);
        _menu.Items.Add(_sparkItem);
        _menu.Items.Add(_horizontalItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => App.ExitCurrent()));

        _menu.Opening += (_, _) => SyncState();
        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.DoubleClick += (_, _) => _window.ShowAndActivate();

        _ = settings;
    }

    private Forms.ToolStripMenuItem CreateCheck(string text, Func<bool> getter, Action<bool> setter)
    {
        var item = new Forms.ToolStripMenuItem(text) { CheckOnClick = true };
        item.Click += (_, _) =>
        {
            setter(item.Checked);
            App.NotifySettingsChanged();
        };
        item.Tag = getter;
        return item;
    }

    public void SyncState()
    {
        _showItem.Text = _window.IsVisible ? "隐藏面板" : "显示面板";
        _topmostItem.Checked = App.CurrentSettings.AlwaysOnTop;
        _lockItem.Checked = App.CurrentSettings.Locked;
        _clickThroughItem.Checked = App.CurrentSettings.ClickThrough;
        _sparkItem.Checked = App.CurrentSettings.ShowSparkline;
        _horizontalItem.Checked = App.CurrentSettings.Horizontal;
        _autoStartItem.Checked = AutoStart.IsRegistered;
    }

    private Icon LoadIcon()
    {
        try
        {
            StreamResourceInfo? res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null)
            {
                _iconStream = new MemoryStream();
                res.Stream.CopyTo(_iconStream);
                _iconStream.Position = 0;
                return new Icon(_iconStream);
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("托盘图标加载失败: " + ex.Message);
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _iconStream?.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 239, 248) : Color.FromArgb(126, 137, 152);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(24, 27, 33);

        public override Color ImageMarginGradientBegin => Color.FromArgb(24, 27, 33);

        public override Color ImageMarginGradientMiddle => Color.FromArgb(24, 27, 33);

        public override Color ImageMarginGradientEnd => Color.FromArgb(24, 27, 33);

        public override Color MenuBorder => Color.FromArgb(64, 70, 84);

        public override Color MenuItemBorder => Color.FromArgb(72, 80, 96);

        public override Color MenuItemSelected => Color.FromArgb(42, 46, 56);

        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(42, 46, 56);

        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(42, 46, 56);

        public override Color SeparatorDark => Color.FromArgb(54, 60, 72);

        public override Color SeparatorLight => Color.FromArgb(54, 60, 72);

        public override Color CheckBackground => Color.FromArgb(60, 66, 80);

        public override Color CheckSelectedBackground => Color.FromArgb(78, 86, 102);

        public override Color CheckPressedBackground => Color.FromArgb(90, 98, 116);
    }
}
