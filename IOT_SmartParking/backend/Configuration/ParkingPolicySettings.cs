namespace SmartParking.Api.Configuration;

public class ParkingPolicySettings
{
    public int CheckInGraceMin { get; set; } = 15;
    public int ParkingTimeoutMin { get; set; } = 10;
    public int HoldMin { get; set; } = 5;
    public int GateTransitSec { get; set; } = 60;
    public int ExtensionMin { get; set; } = 5;
    public int MaxExtensions { get; set; } = 1;
    public int MaxTotalExtensionMin { get; set; } = 15;
    public decimal PenaltyPerMinute { get; set; } = 200m;
    public decimal OverstayPenaltyPerMinute
    {
        get => PenaltyPerMinute;
        set => PenaltyPerMinute = value;
    }
    public int OverstayGraceMin { get; set; } = 15;
    public int ScanRetryLimit { get; set; } = 3;
    public double BookableRatio { get; set; } = 0.7;
    public int MinGapBetweenBookingsMin { get; set; } = 15;
    public int MaxInLotSearching { get; set; } = 3;
    public decimal PricePerHour { get; set; } = 10000m;
    public string NoSlotPolicy { get; set; } = "FreeExit"; // "FreeExit" or "PartialRefund"
    public string MqttTopicPrefix { get; set; } = "dacn-smartparking/lot1";
    public double OccupiedMaxCm { get; set; } = 40.0;
    public double PartialMaxCm { get; set; } = 80.0;
    public int OverlapWindowSec { get; set; } = 30;
    public bool DemoMode { get; set; } = false;
    public double TimeScale { get; set; } = 1.0;

    // Giờ hoạt động của bãi đỗ xe (múi giờ Việt Nam UTC+7)
    public int OpenHour { get; set; } = 6;
    public int CloseHour { get; set; } = 23;

    // Cấu hình CashCode
    public int CashCodeExpiryMin { get; set; } = 30;
    public int CashCodeMaxFailedAttempts { get; set; } = 5;

    // Khóa bí mật cho Webhook HMAC-SHA256 (mặc định cho Dev/Demo, production đặt trong user-secrets)
    public string WebhookSecret { get; set; } = "SmartParking_WebhookSecret_Key_For_Hmac_2026!";
}
