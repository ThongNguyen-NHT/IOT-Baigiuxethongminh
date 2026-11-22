using Microsoft.AspNetCore.SignalR;
using SmartParking.Api.Hubs;

namespace SmartParking.Api.Services;

public class ParkingNotificationService : IParkingNotificationService
{
    private readonly IHubContext<ParkingHub> _hubContext;
    private readonly ILogger<ParkingNotificationService> _logger;

    public ParkingNotificationService(
        IHubContext<ParkingHub> hubContext,
        ILogger<ParkingNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifySlotChangedAsync(string slotId, string status, decimal? distanceCm, string? plate = null, long? orderId = null)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("SlotUpdated", new
            {
                slotId,
                status,
                distanceCm,
                plate,
                orderId,
                updatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR SlotUpdated cho {SlotId}", slotId);
        }
    }

    public async Task NotifySlotsReloadAsync()
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("SlotsReload");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR SlotsReload");
        }
    }

    public async Task NotifyTimeChangedAsync(DateTimeOffset currentTime, TimeSpan offset)
    {
        try
        {
            var vnTime = currentTime.ToOffset(TimeSpan.FromHours(7));
            await _hubContext.Clients.All.SendAsync("TimeUpdated", new
            {
                utc = currentTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                local = vnTime.ToString("yyyy-MM-ddTHH:mm:ss+07:00"),
                display = vnTime.ToString("HH:mm:ss dd/MM/yyyy"),
                offsetMinutes = offset.TotalMinutes
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR TimeUpdated");
        }
    }

    public async Task NotifySystemLogAsync(string eventType, string level, string message, string source = "BACKEND")
    {
        try
        {
            var vnTime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
            await _hubContext.Clients.All.SendAsync("SystemLog", new
            {
                eventType,
                level,
                message,
                source,
                time = vnTime.ToString("HH:mm:ss")
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR SystemLog");
        }
    }

    public async Task NotifyAlertAsync(string alertType, string message, object? details = null)
    {
        try
        {
            var vnTime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
            await _hubContext.Clients.All.SendAsync("Alert", new
            {
                alertType,
                message,
                details,
                time = vnTime.ToString("HH:mm:ss")
            });
            await _hubContext.Clients.All.SendAsync("AlertRaised", alertType, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR Alert");
        }
    }

    public async Task NotifyBarrierAsync(string gate, string state)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("BarrierUpdated", new
            {
                gate,
                state,
                updatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR BarrierUpdated");
        }
    }

    public async Task NotifyOrderStatusChangedAsync(long orderId, string status, string? message = null, object? details = null)
    {
        try
        {
            var vnTime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
            await _hubContext.Clients.All.SendAsync("OrderStatusUpdated", new
            {
                orderId,
                status,
                message,
                details,
                time = vnTime.ToString("HH:mm:ss"),
                updatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR OrderStatusUpdated cho đơn {OrderId}", orderId);
        }
    }

    public async Task NotifySummaryUpdatedAsync(object summary)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("SummaryUpdated", summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gửi SignalR SummaryUpdated");
        }
    }
}
