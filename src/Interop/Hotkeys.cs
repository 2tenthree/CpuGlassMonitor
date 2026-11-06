using System.Runtime.InteropServices;

namespace CpuGlassMonitor.Interop;

/// <summary>
/// 全局热键。
///
/// 为什么需要：托盘图标在某些机器上会被 Win11 收进"隐藏的图标"浮窗，或者开机自启时
/// 资源管理器还没准备好而丢失，用户就"找不到程序了"。有了全局热键，即使托盘图标不可见，
/// 也能随时把面板叫回来 / 打开设置 —— 不再依赖托盘。
/// </summary>
internal static class Hotkeys
{
    public const int WmHotkey = 0x0312;

    public const int TogglePanelId = 0xA11;
    public const int OpenSettingsId = 0xA12;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>Ctrl+Alt+M</summary>
    public const uint TogglePanelKey = 0x4D;

    /// <summary>Ctrl+Alt+S</summary>
    public const uint OpenSettingsKey = 0x53;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
