using Microsoft.Extensions.Options;
using SmartParking.Api.Configuration;

namespace SmartParking.Api.Services;

/// <summary>
/// TimeProvider ho tro he so tang toc thoi gian (TimeScale) va tua thoi gian thu cong (Time Travel)
/// phuc vu demo va kiem thu cac tinh huong nghiep vu (het han giu cho, NO_SHOW, kẹt xe...).
/// </summary>
public class ScaledTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _startRealTime;
    private readonly DateTimeOffset _startSimTime;
    private readonly IOptionsMonitor<ParkingPolicySettings> _policyOptions;
    private TimeSpan _manualOffset = TimeSpan.Zero;
    private readonly object _lock = new();

    public ScaledTimeProvider(IOptionsMonitor<ParkingPolicySettings> policyOptions)
    {
        _policyOptions = policyOptions;
        _startRealTime = DateTimeOffset.UtcNow;
        _startSimTime = DateTimeOffset.UtcNow;
    }

    public double TimeScale => Math.Max(0.1, _policyOptions.CurrentValue.TimeScale);
    public TimeSpan ManualOffset { get { lock (_lock) return _manualOffset; } }

    public void Advance(TimeSpan duration)
    {
        lock (_lock)
        {
            _manualOffset = _manualOffset.Add(duration);
        }
    }

    public void ResetOffset()
    {
        lock (_lock)
        {
            _manualOffset = TimeSpan.Zero;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            double scale = TimeScale;
            DateTimeOffset baseTime;
            if (Math.Abs(scale - 1.0) < 0.001)
            {
                baseTime = DateTimeOffset.UtcNow;
            }
            else
            {
                TimeSpan realElapsed = DateTimeOffset.UtcNow - _startRealTime;
                long scaledTicks = (long)(realElapsed.Ticks * scale);
                baseTime = _startSimTime.AddTicks(scaledTicks);
            }
            return baseTime.Add(_manualOffset);
        }
    }
}
