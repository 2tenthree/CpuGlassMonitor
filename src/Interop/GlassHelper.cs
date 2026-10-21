using System.Runtime.InteropServices;
using System.Windows.Media;

namespace CpuGlassMonitor.Interop;

public enum GlassMode
{
    /// <summary>Win10/11 的 SetWindowCompositionAttribute 亚克力。</summary>
    Acrylic,

    /// <summary>Win11 22H2+ 的 DWM 系统背景（亚克力）。</summary>
    SystemBackdrop,

    /// <summary>分层窗口下的传统 DWM 模糊（无 tint，透明度交给 XAML 蒙版）。</summary>
    BlurBehind,

    /// <summary>系统不支持模糊，退化为半透明纯色。</summary>
    Solid
}

/// <summary>窗口毛玻璃（亚克力模糊）效果的应用与降级。</summary>
internal static class GlassHelper
{
    /// <summary>应用亚克力模糊；返回实际生效的模式。</summary>
    public static GlassMode Apply(IntPtr hwnd, Color tint, double tintAlpha, bool roundCorners = true)
    {
        GetWindowCompositionAttribute(hwnd, out int before);
        _ = before;

        if (roundCorners)
        {
            Native.SetDwmInt(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        }

        Native.SetDwmInt(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        // 不要系统绘制的描边，边框由 XAML 自己画
        Native.SetDwmInt(hwnd, Native.DWMWA_BORDER_COLOR, unchecked((int)Native.DWMWA_COLOR_NONE));

        byte a = (byte)Math.Clamp(Math.Round(tintAlpha * 255.0), 0, 255);
        uint gradient = (uint)((a << 24) | (tint.B << 16) | (tint.G << 8) | tint.R);

        var accent = new Native.AccentPolicy
        {
            AccentState = Native.AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = gradient,
            AnimationId = 0
        };

        if (TrySetAccent(hwnd, accent) && GetWindowCompositionAttribute(hwnd, out int state) &&
            state == (int)Native.AccentState.EnableAcrylicBlurBehind)
        {
            return GlassMode.Acrylic;
        }

        // Win11 22H2+ 退路：DWM 系统亚克力背景（窗口不透明时才有意义）
        if (Native.SetDwmInt(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_ACRYLIC))
        {
            return GlassMode.SystemBackdrop;
        }

        return GlassMode.Solid;
    }

    /// <summary>
    /// 分层窗口（AllowsTransparency=True）专用。
    /// 注意：分层窗口拿不到真正可控的 DWM 模糊——系统的 blur/acrylic 会叠一层浓度固定的
    /// tint，把面板锁在"看起来不透明"。所以只在"本来就该接近不透明"的区间借它做磨砂质感，
    /// 其余区间直接关掉 accent，让 XAML 的 alpha 决定能看穿多少（这才是真正的透明度）。
    /// </summary>
    public static GlassMode ApplyLayered(IntPtr hwnd, Color tint, double tintAlpha)
    {
        Native.SetDwmInt(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.SetDwmInt(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwmInt(hwnd, Native.DWMWA_BORDER_COLOR, unchecked((int)Native.DWMWA_COLOR_NONE));

        byte a = (byte)Math.Clamp(Math.Round(tintAlpha * 255.0), 0, 255);
        uint gradient = (uint)((a << 24) | (tint.B << 16) | (tint.G << 8) | tint.R);

        var state = tintAlpha >= 0.82
            ? Native.AccentState.EnableAcrylicBlurBehind
            : Native.AccentState.Disabled;

        var accent = new Native.AccentPolicy
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = gradient,
            AnimationId = 0
        };

        _ = TrySetAccent(hwnd, accent);

        switch (state)
        {
            case Native.AccentState.EnableAcrylicBlurBehind:
                return GlassMode.Acrylic;
        }

        return GlassMode.Solid;
    }

    private static bool TrySetAccent(IntPtr hwnd, Native.AccentPolicy accent)
    {
        int size = Marshal.SizeOf<Native.AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Native.WindowCompositionAttributeData
            {
                Attribute = Native.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            return Native.SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static bool GetWindowCompositionAttribute(IntPtr hwnd, out int accentState)
    {
        accentState = 0;
        int size = Marshal.SizeOf<Native.AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            var data = new Native.WindowCompositionAttributeData
            {
                Attribute = Native.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            if (Native.GetWindowCompositionAttribute(hwnd, ref data) == 0) return false;
            var policy = Marshal.PtrToStructure<Native.AccentPolicy>(ptr);
            accentState = (int)policy.AccentState;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>把窗口设为鼠标穿透（true）或可交互（false）。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        int ex = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE);
        int updated = enabled
            ? ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE
            : ex & ~(Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
        if (updated != ex)
        {
            Native.SetWindowLongSafe(hwnd, Native.GWL_EXSTYLE, updated);
        }
    }

    /// <summary>让窗口不进入 Alt+Tab / 任务栏。</summary>
    public static void HideFromAltTab(IntPtr hwnd)
    {
        int ex = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongSafe(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
    }

    public static void ExtendGlassFrame(IntPtr hwnd)
    {
        var margins = new Native.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }
}
