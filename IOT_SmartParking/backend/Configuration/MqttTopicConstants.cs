namespace SmartParking.Api.Configuration;

/// <summary>
/// Hằng số quy ước chuẩn các Topic MQTT của toàn bộ hệ thống Smart Parking.
/// Một nguồn duy nhất (Single Source of Truth) làm hợp đồng kết nối giữa Backend API, Dashboard và Firmware ESP32 (Wokwi).
/// </summary>
public static class MqttTopicConstants
{
    // 1. Ô đỗ (Slots)
    public const string SlotStateFilter = "slot/+/state";       // Thiết bị -> Server (Cự ly cảm biến siêu âm)
    public const string SlotCmdFilter = "slot/+/cmd";           // Server -> Thiết bị (Lệnh điều khiển đèn ô đỗ)

    public static string SlotState(string prefix, string slotId) => $"{prefix.TrimEnd('/')}/slot/{slotId}/state";
    public static string SlotCmd(string prefix, string slotId) => $"{prefix.TrimEnd('/')}/slot/{slotId}/cmd";

    // 2. Dẫn đường & Hiển thị (Guide & Display)
    public static string GuideCmd(string prefix) => $"{prefix.TrimEnd('/')}/guide/cmd";       // Server -> Thiết bị (Dải LED NeoPixel dẫn đường)
    public static string DisplayCmd(string prefix) => $"{prefix.TrimEnd('/')}/display/cmd";   // Server -> Thiết bị (Màn hình LCD 1602 / 2004)

    // 3. Cổng vào / Cổng ra (Gate)
    public const string GateEventFilter = "gate/+/event";
    public static string GateInCmd(string prefix) => $"{prefix.TrimEnd('/')}/gate/in/cmd";     // Server -> Thiết bị (OPEN, HOLD, UNHOLD)
    public static string GateInEvent(string prefix) => $"{prefix.TrimEnd('/')}/gate/in/event"; // Thiết bị -> Server ({"type":"pass","eventId":"..."})
    public static string GateOutCmd(string prefix) => $"{prefix.TrimEnd('/')}/gate/out/cmd";   // Server -> Thiết bị (OPEN)
    public static string GateOutEvent(string prefix) => $"{prefix.TrimEnd('/')}/gate/out/event"; // Thiết bị -> Server ({"type":"leave","eventId":"..."})

    // 4. Trạm kiểm soát nội bộ (Checkpoint)
    public const string CheckpointEventFilter = "checkpoint/+/event";
    public static string CheckpointEvent(string prefix, string cpId) => $"{prefix.TrimEnd('/')}/checkpoint/{cpId}/event"; // Thiết bị -> Server ({"type":"pass","eventId":"..."})

    // 5. Trạng thái thiết bị & Nguồn (Device & Power)
    public const string DeviceStatusFilter = "device/+/status";
    public static string DeviceStatus(string prefix, string devId) => $"{prefix.TrimEnd('/')}/device/{devId}/status"; // Thiết bị (LWT retained) -> Server (ONLINE, OFFLINE)
    public static string DevicePower(string prefix, string devId) => $"{prefix.TrimEnd('/')}/device/{devId}/power";   // Server <-> Thiết bị (Điều khiển nguồn / báo mất điện)

    // 6. Đồng bộ, Báo cáo & Cảnh báo (System)
    public static string Resync(string prefix) => $"{prefix.TrimEnd('/')}/resync";             // Thiết bị <-> Server (Yêu cầu đồng bộ / snapshot)
    public static string Summary(string prefix) => $"{prefix.TrimEnd('/')}/summary";           // Server -> Dashboard (Thống kê realtime)
    public const string AlertFilter = "alert";
    public static string Alert(string prefix) => $"{prefix.TrimEnd('/')}/alert";               // Backend <-> ESP32 / Dashboard (Cảnh báo: UNAUTHORIZED_ENTRY, OVERLAP, đỗ chui)

    // 7. Xác nhận sự kiện (ACK)
    public const string AckFilter = "ack";
    public static string Ack(string prefix) => $"{prefix.TrimEnd('/')}/ack";                   // Server -> Thiết bị / Simulator ({"eventId":"..."})
}
