namespace SmartParking.Api.Services;

public enum ParkingSyncStatus
{
    Applied,
    Duplicate,
    Deferred,
    Rejected
}

public record ParkingSyncResult(
    ParkingSyncStatus Status,
    string Message)
{
    public static ParkingSyncResult Applied(
        string message = "Applied") =>
        new(ParkingSyncStatus.Applied, message);

    public static ParkingSyncResult Duplicate(
        string message = "Already applied") =>
        new(ParkingSyncStatus.Duplicate, message);

    public static ParkingSyncResult Deferred(
        string message = "Dependency missing") =>
        new(ParkingSyncStatus.Deferred, message);

    public static ParkingSyncResult Rejected(
        string message = "Invalid event") =>
        new(ParkingSyncStatus.Rejected, message);
}
