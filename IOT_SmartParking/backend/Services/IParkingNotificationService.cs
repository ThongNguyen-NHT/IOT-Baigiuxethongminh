namespace SmartParking.Api.Services;

public interface IParkingNotificationService
{
    Task NotifySlotChangedAsync(string slotId, string status, decimal? distanceCm, string? plate = null, long? orderId = null);
    Task NotifySlotsReloadAsync();
    Task NotifyTimeChangedAsync(DateTimeOffset currentTime, TimeSpan offset);
    Task NotifySystemLogAsync(string eventType, string level, string message, string source = "BACKEND");
    Task NotifyAlertAsync(string alertType, string message, object? details = null);
    Task NotifyBarrierAsync(string gate, string state);
    Task NotifyOrderStatusChangedAsync(long orderId, string status, string? message = null, object? details = null);
    Task NotifySummaryUpdatedAsync(object summary);
}
