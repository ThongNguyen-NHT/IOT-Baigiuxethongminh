using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

namespace SmartParking.Simulator;

public class Program
{
    private static IMqttClient? _mqttClient;
    private static string _brokerHost = "broker.hivemq.com";
    private static int _brokerPort = 1883;
    private static string _prefix = "dacn-smartparking/lot1";
    private static string _clientId = "sim-device-" + Guid.NewGuid().ToString("N")[..6];

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("==================================================================");
        Console.WriteLine("    SMART PARKING - THIẾT BỊ GIẢ LẬP IOT (MQTT SIMULATOR)       ");
        Console.WriteLine("==================================================================");

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_brokerHost, _brokerPort)
            .WithClientId(_clientId)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();

        Console.Write($"[SIM] Đang kết nối tới MQTT Broker {_brokerHost}:{_brokerPort}... ");
        try
        {
            await _mqttClient.ConnectAsync(options);
            Console.WriteLine("THÀNH CÔNG!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"THẤT BẠI ({ex.Message}).");
            Console.WriteLine("[SIM] Tiếp tục với chế độ giả lập offline/retry...");
        }

        // Đăng ký nhận tin nhắn phản hồi từ backend để hiển thị kết quả kiểm thử
        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[MQTT <- BACKEND] Topic: {topic}\nPayload: {payload}");
            Console.ResetColor();
            Console.Write("\nSim> ");
            return Task.CompletedTask;
        };

        try
        {
            await _mqttClient.SubscribeAsync($"{_prefix}/#");
            Console.WriteLine($"[SIM] Đã subscribe topic '{_prefix}/#' để lắng nghe phản hồi.");
        }
        catch
        {
            // Ignore subscribe failure in offline demo
        }

        if (args.Length > 0)
        {
            await ExecuteCommandAsync(string.Join(" ", args));
            return;
        }

        PrintHelp();

        while (true)
        {
            Console.Write("\nSim> ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;

            string cmd = input.Trim();
            if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase) || cmd.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await ExecuteCommandAsync(cmd);
        }

        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync();
        }
        Console.WriteLine("[SIM] Đã tắt công cụ giả lập.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("\nDANH SÁCH LỆNH CÓ THỂ THỰC HIỆN:");
        Console.WriteLine("  1. state <slotId> <distanceCm>        : Gửi cự ly cảm biến ô đỗ (ví dụ: state E1 30.5)");
        Console.WriteLine("  2. pass <orderId>                     : Xe qua cổng vào barie (PASS|orderId)");
        Console.WriteLine("  3. leave <orderId>                    : Xe qua cổng ra barie (LEAVE|orderId)");
        Console.WriteLine("  4. checkpoint <cpId> [orderId]        : Xe qua checkpoint nội bộ (ví dụ: checkpoint CP1 101)");
        Console.WriteLine("  5. status <deviceId> <ONLINE|OFFLINE> : Gửi trạng thái LWT (ví dụ: status esp32_lot1 OFFLINE)");
        Console.WriteLine("  6. resync                             : Gửi yêu cầu resync đồng bộ dữ liệu");
        Console.WriteLine("  7. snapshot                           : Gửi snapshot cảm biến toàn bộ các ô hàng E");
        Console.WriteLine("  8. scenario <1|2|3|4>                 : Chạy kịch bản tự động:");
        Console.WriteLine("       - scenario 1: Xe vào bình thường (Pass cổng -> Checkpoint -> Vào ô)");
        Console.WriteLine("       - scenario 2: Xe đỗ lấn vạch (OVERLAP giữa E1 và E2)");
        Console.WriteLine("       - scenario 3: Mất điện (LWT OFFLINE) rồi online lại (ONLINE + Resync)");
        Console.WriteLine("       - scenario 4: Bãi đông xe (Gate HOLD) và tự mở lại khi xe vào ô");
        Console.WriteLine("  help                                  : Xem lại trợ giúp");
        Console.WriteLine("  exit                                  : Thoát chương trình");
    }

    public static async Task ExecuteCommandAsync(string commandLine)
    {
        string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string verb = parts[0].ToLowerInvariant();

        try
        {
            switch (verb)
            {
                case "help":
                    PrintHelp();
                    break;

                case "state":
                    if (parts.Length < 3)
                    {
                        Console.WriteLine("Cú pháp: state <slotId> <distanceCm> (ví dụ: state E1 35.0)");
                        return;
                    }
                    string slotId = parts[1].ToUpperInvariant();
                    if (!double.TryParse(parts[2], out double dist)) dist = 30.0;
                    await PublishStateAsync(slotId, dist);
                    break;

                case "pass":
                    await PublishGateInPassAsync();
                    break;

                case "leave":
                    await PublishGateOutLeaveAsync();
                    break;

                case "checkpoint":
                    string cpId = parts.Length > 1 ? parts[1].ToUpperInvariant() : "CP1";
                    await PublishCheckpointAsync(cpId);
                    break;

                case "status":
                    string devId = parts.Length > 1 ? parts[1] : "esp32_row_e";
                    string devState = parts.Length > 2 ? parts[2].ToUpperInvariant() : "ONLINE";
                    await PublishDeviceStatusAsync(devId, devState);
                    break;

                case "resync":
                    await PublishResyncAsync();
                    break;

                case "snapshot":
                    string row = parts.Length > 1 ? parts[1].ToUpperInvariant() : "E";
                    await PublishSnapshotAsync(row);
                    break;

                case "scenario":
                    string sc = parts.Length > 1 ? parts[1].ToLowerInvariant() : "1";
                    await RunScenarioAsync(sc);
                    break;

                default:
                    Console.WriteLine($"Lệnh không hợp lệ: '{verb}'. Gõ 'help' để xem danh sách.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[LỖI]: {ex.Message}");
            Console.ResetColor();
        }
    }

    public static async Task PublishStateAsync(string slotId, double distanceCm)
    {
        string topic = $"{_prefix}/slot/{slotId}/state";
        string payload = JsonSerializer.Serialize(new
        {
            spot = slotId,
            distanceCm = Math.Round(distanceCm, 1)
        });
        await PublishAsync(topic, payload, retain: true);
        Console.WriteLine($"[SIM -> MQTT] Gửi trạng thái ô: {topic} => {payload}");
    }

    public static async Task PublishGateInPassAsync()
    {
        string topic = $"{_prefix}/gate/in/event";
        string eventId = "in_evt_" + Guid.NewGuid().ToString("N")[..8];
        string payload = JsonSerializer.Serialize(new { type = "pass", eventId });
        await PublishAsync(topic, payload, retain: false);
        Console.WriteLine($"[SIM -> MQTT] Gửi sự kiện qua cổng vào: {topic} => {payload}");
    }

    public static async Task PublishGateOutLeaveAsync()
    {
        string topic = $"{_prefix}/gate/out/event";
        string eventId = "out_evt_" + Guid.NewGuid().ToString("N")[..8];
        string payload = JsonSerializer.Serialize(new { type = "leave", eventId });
        await PublishAsync(topic, payload, retain: false);
        Console.WriteLine($"[SIM -> MQTT] Gửi sự kiện qua cổng ra: {topic} => {payload}");
    }

    public static async Task PublishCheckpointAsync(string cpId)
    {
        string topic = $"{_prefix}/checkpoint/{cpId}/event";
        string eventId = "cp_evt_" + Guid.NewGuid().ToString("N")[..8];
        string payload = JsonSerializer.Serialize(new { type = "pass", eventId });
        await PublishAsync(topic, payload, retain: false);
        Console.WriteLine($"[SIM -> MQTT] Gửi sự kiện checkpoint {cpId}: {topic} => {payload}");
    }

    public static async Task PublishDeviceStatusAsync(string deviceId, string status)
    {
        string topic = $"{_prefix}/device/{deviceId}/status";
        await PublishAsync(topic, status, retain: true);
        Console.WriteLine($"[SIM -> MQTT] Gửi trạng thái thiết bị LWT: {topic} => {status}");
    }

    public static async Task PublishResyncAsync()
    {
        string topic = $"{_prefix}/resync";
        string payload = JsonSerializer.Serialize(new { action = "REQUEST_RESYNC" });
        await PublishAsync(topic, payload, retain: false);
        Console.WriteLine($"[SIM -> MQTT] Gửi yêu cầu Resync: {topic} => {payload}");
    }

    public static async Task PublishSnapshotAsync(string row = "E")
    {
        string r = string.IsNullOrWhiteSpace(row) ? "E" : row.Trim().ToUpperInvariant();
        Console.WriteLine($"[SIM -> MQTT] Gửi Snapshot cảm biến cho hàng {r} (8 ô {r}1..{r}8)...");
        for (int i = 1; i <= 8; i++)
        {
            string slotId = $"{r}{i}";
            double dist = (i == 3) ? 30.0 : 200.0; // Giả lập ô 3 có xe, 7 ô còn lại trống
            await PublishStateAsync(slotId, dist);
            await Task.Delay(15);
        }
    }

    private static async Task RunScenarioAsync(string scenario)
    {
        switch (scenario)
        {
            case "1":
            case "normal":
                Console.WriteLine("\n--- [KỊCH BẢN 1]: XE VÀO BÌNH THƯỜNG ---");
                Console.WriteLine("Bước 1: Xe qua cổng vào barie (PASS)...");
                await PublishGateInPassAsync();
                await Task.Delay(1500);

                Console.WriteLine("Bước 2: Xe đi qua trạm kiểm soát CP1 (Gia hạn thời gian tìm ô)...");
                await PublishCheckpointAsync("CP1");
                await Task.Delay(1500);

                Console.WriteLine("Bước 3: Xe lùi an toàn vào ô E1 (Cảm biến báo 28.5 cm)...");
                await PublishStateAsync("E1", 28.5);
                Console.WriteLine("[KỊCH BẢN 1 HOÀN TẤT]: Backend ghi nhận xe PARKED, đèn LED chuyển ĐỎ.");
                break;

            case "2":
            case "overlap":
                Console.WriteLine("\n--- [KỊCH BẢN 2]: XE ĐỖ LẤN VẠCH (OVERLAP) ---");
                Console.WriteLine("Bước 1: Cảm biến ô E1 phát hiện vật thể mép vạch (cự ly 65cm - PARTIAL)...");
                await PublishStateAsync("E1", 65.0);
                await Task.Delay(1000);

                Console.WriteLine("Bước 2: Cảm biến ô liền kề E2 đồng thời phát hiện vật thể (cự ly 70cm - PARTIAL)...");
                await PublishStateAsync("E2", 70.0);
                Console.WriteLine("[KỊCH BẢN 2 HOÀN TẤT]: Backend phát hiện cặp ô lấn vạch (OVERLAP), bật đèn VÀNG và phát cảnh báo!");
                break;

            case "3":
            case "outage":
                Console.WriteLine("\n--- [KỊCH BẢN 3]: MẤT ĐIỆN VÀ ONLINE LẠI (LWT & RESYNC) ---");
                Console.WriteLine("Bước 1: Thiết bị ESP32 mất điện/đứt mạng đột ngột (LWT báo OFFLINE)...");
                await PublishDeviceStatusAsync("esp32_lot1", "OFFLINE");
                Console.WriteLine(">> Backend nhận LWT, đánh dấu toàn bộ ô hàng E sang UNKNOWN.");
                await Task.Delay(2000);

                Console.WriteLine("Bước 2: Thiết bị có điện trở lại, kết nối MQTT broker và báo ONLINE...");
                await PublishDeviceStatusAsync("esp32_lot1", "ONLINE");
                await Task.Delay(1000);

                Console.WriteLine("Bước 3: Thiết bị gửi tin nhắn Resync yêu cầu Backend kiểm toán trạng thái...");
                await PublishResyncAsync();
                await Task.Delay(1000);

                Console.WriteLine("Bước 4: Thiết bị gửi Snapshot cảm biến thực tế để khôi phục trạng thái...");
                await PublishSnapshotAsync();
                Console.WriteLine("[KỊCH BẢN 3 HOÀN TẤT]: Backend hoàn tất Audit và khôi phục ô đỗ.");
                break;

            case "4":
            case "congestion":
                Console.WriteLine("\n--- [KỊCH BẢN 4]: BÃI ĐÔNG (HOLD) VÀ TỰ MỞ LẠI KHI XE VÀO Ô ---");
                Console.WriteLine("Bước 1: Bãi đang có nhiều xe tìm chỗ (MaxInLotSearching = 3)...");
                Console.WriteLine("Bước 2: Một xe tìm thấy ô và đỗ an toàn vào ô E1 (cự ly 30cm)...");
                await PublishStateAsync("E1", 30.0);
                Console.WriteLine("[KỊCH BẢN 4 HOÀN TẤT]: Backend phát hiện số xe tìm chỗ giảm, tự động phát lệnh UNHOLD/OPEN cho cổng vào!");
                break;

            default:
                Console.WriteLine($"Kịch bản '{scenario}' không tồn tại. Chọn 1, 2, 3 hoặc 4.");
                break;
        }
    }

    private static async Task PublishAsync(string topic, string payload, bool retain)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            Console.WriteLine($"[OFFLINE MODE] Msg to {topic}: {payload}");
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _mqttClient.PublishAsync(message);
    }
}
