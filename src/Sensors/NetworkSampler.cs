using System.Diagnostics;
using System.Net.NetworkInformation;

namespace CpuGlassMonitor.Sensors;

/// <summary>
/// 网速采样：把所有「已启用、非回环」网卡的累计字节数做差值，换算成每秒字节数。
/// 不依赖 LibreHardwareMonitor（它在 Windows 上不提供网络吞吐），也不需要管理员权限。
/// </summary>
public sealed class NetworkSampler
{
    private const double EmaAlpha = 0.55;      // 平滑系数，避免数字每秒跳
    private const double MaxGapSeconds = 5.0;  // 间隔太大（睡眠/休眠）时重新取基准，不产生尖峰

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private NetworkInterface[] _interfaces = Array.Empty<NetworkInterface>();
    private int _refreshAfter;

    private long _lastSent = -1;
    private long _lastReceived = -1;
    private double _lastSeconds = -1;
    private double _down;
    private double _up;

    /// <summary>当前下行速度（字节/秒）。</summary>
    public double DownBytesPerSecond => _down;

    /// <summary>当前上行速度（字节/秒）。</summary>
    public double UpBytesPerSecond => _up;

    /// <summary>采一帧。第一次调用只记录基准，返回 0。</summary>
    public void Sample()
    {
        try
        {
            if (_interfaces.Length == 0 || --_refreshAfter <= 0)
            {
                RefreshInterfaces();
                _refreshAfter = 30;
            }

            long sent = 0;
            long received = 0;
            foreach (NetworkInterface ni in _interfaces)
            {
                try
                {
                    IPInterfaceStatistics stats = ni.GetIPStatistics();
                    sent += stats.BytesSent;
                    received += stats.BytesReceived;
                }
                catch
                {
                    // 网卡刚消失/驱动异常：跳过这一块
                }
            }

            double now = _clock.Elapsed.TotalSeconds;
            if (_lastSeconds < 0)
            {
                _lastSent = sent;
                _lastReceived = received;
                _lastSeconds = now;
                return;
            }

            double dt = now - _lastSeconds;
            if (dt <= 0.05) return;

            double downInstant = Math.Max(0, received - _lastReceived) / dt;
            double upInstant = Math.Max(0, sent - _lastSent) / dt;

            _lastSent = sent;
            _lastReceived = received;

            if (dt > MaxGapSeconds)
            {
                // 从睡眠里醒来：直接以本次为基准，别把累计差值算成一个巨大的尖峰
                _lastSeconds = now;
                _down = downInstant;
                _up = upInstant;
                return;
            }

            _lastSeconds = now;
            _down = _down * (1 - EmaAlpha) + downInstant * EmaAlpha;
            _up = _up * (1 - EmaAlpha) + upInstant * EmaAlpha;
        }
        catch
        {
            // 采样失败不影响其它传感器
        }
    }

    private void RefreshInterfaces()
    {
        try
        {
            _interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Where(ni => ni.Supports(NetworkInterfaceComponent.IPv4) || ni.Supports(NetworkInterfaceComponent.IPv6))
                .ToArray();
        }
        catch
        {
            _interfaces = Array.Empty<NetworkInterface>();
        }
    }
}
