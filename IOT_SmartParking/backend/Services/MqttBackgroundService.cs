
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Dtos;
using SmartParking.Api.Models;

namespace SmartParking.Api.Services;

public class MqttBackgroundService : BackgroundService, IMqttPublisher
{
    private readonly ILogger<MqttBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MqttSettings _settings;

    private readonly IMqttClient _client;
    
    private readonly SemaphoreSlim _messageLock = new(1, 1);
    private int _isSubscribing = 0;
    private static readonly ConcurrentDictionary<string, (double DistanceCm, DateTime Timestamp)> _recentSlotReadings = new();
    private static readonly int[] ReconnectBackoffs = { 1, 2, 5, 10, 30 };

    public bool IsConnected => _client != null && _client.IsConnected;

    public static void RecordSlotReading(string slotId, double distanceCm, DateTime timestamp)
    {
        _recentSlotReadings[slotId] = (distanceCm, timestamp);
    }

    public static (double DistanceCm, DateTime Timestamp)? GetRecentReading(string slotId)
    {
        if (_recentSlotReadings.TryGetValue(slotId, out var reading))
            return reading;
        return null;
    }

    public static void ClearRecentReadings() => _recentSlotReadings.Clear();

    public static int GetRecentReadingsCount() => _recentSlotReadings.Count;

    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        if (_client == null || !_client.IsConnected)
        {
            _logger.LogWarning("Không thể publish MQTT vì client chưa kết nối. Topic: {Topic}", topic);
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message);
        _logger.LogInformation("MQTT Published -> {Topic}: {Payload}", topic, payload);
    }


    public MqttBackgroundService(
        ILogger<MqttBackgroundService> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<MqttSettings> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _settings = options.Value;

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ConnectedAsync += async args =>
        {
            _logger.LogInformation(
                "MQTT connected to {Host}:{Port}",
                _settings.Host,
                _settings.Port);

            await SubscribeTopicsAsync();
        };

        _client.DisconnectedAsync += args =>
        {
            _logger.LogWarning(
                "MQTT disconnected. Reason: {Reason}. Reconnecting in background loop...",
                args.Reason);

            return Task.CompletedTask;
        };

        _client.ApplicationMessageReceivedAsync +=
            HandleMessageAsync;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting Smart Parking MQTT Subscriber");

        int reconnectAttempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    int targetPort = _settings.UseTls ? _settings.TlsPort : _settings.Port;
                    var optionsBuilder = new MqttClientOptionsBuilder()
                        .WithClientId(_settings.ClientId)
                        .WithTcpServer(
                            _settings.Host,
                            targetPort)
                        .WithCleanSession()
                        .WithKeepAlivePeriod(
                            TimeSpan.FromSeconds(30));

                    if (_settings.UseTls)
                    {
                        optionsBuilder.WithTlsOptions(tls => tls.UseTls());
                    }

                    if (!string.IsNullOrEmpty(_settings.Username))
                    {
                        optionsBuilder.WithCredentials(_settings.Username, _settings.Password);
                    }

                    var options = optionsBuilder.Build();

                    await _client.ConnectAsync(
                        options,
                        stoppingToken);

                    _logger.LogInformation("MQTT client đã kết nối tới {Host}:{Port}. Đang subscribe lại toàn bộ topics...", _settings.Host, targetPort);
                    await SubscribeTopicsAsync();
                    reconnectAttempts = 0;

                    // Khởi tạo/khôi phục cự ly RAM và tính toán lại OVERLAP từ DB khi vừa kết nối
                    try
                    {
                        using var initScope = _scopeFactory.CreateScope();
                        var db = initScope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
                        var notifier = initScope.ServiceProvider.GetService<IParkingNotificationService>();
                        await RestoreAndRecalculateOverlapFromDbAsync(db, this, notifier, _settings.TopicPrefix);
                        await PublishSummaryAsync(db);
                    }
                    catch (Exception initEx)
                    {
                        _logger.LogWarning(initEx, "Lỗi khôi phục RAM OVERLAP lúc khởi động MQTT.");
                    }
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                int delaySec = reconnectAttempts < ReconnectBackoffs.Length 
                    ? ReconnectBackoffs[reconnectAttempts] 
                    : ReconnectBackoffs[^1];
                reconnectAttempts++;

                _logger.LogWarning(
                    "Không thể kết nối MQTT Broker ({Host}:{Port}). Thử lại sau {Delay}s (Lần {Attempts}): {Message}",
                    _settings.Host,
                    _settings.Port,
                    delaySec,
                    reconnectAttempts,
                    ex.Message);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Error while disconnecting MQTT");
            }
        }

        _client.Dispose();
    }

    internal async Task SubscribeTopicsAsync()
    {
        if (!_client.IsConnected)
            return;

        if (Interlocked.CompareExchange(ref _isSubscribing, 1, 0) != 0)
        {
            _logger.LogDebug("Quá trình subscribe topics đang diễn ra, bỏ qua yêu cầu trùng lặp.");
            return;
        }

        string prefix = _settings.TopicPrefix.TrimEnd('/');

        string[] topics =
        {
            $"{prefix}/gate/+/event",
            $"{prefix}/checkpoint/+/event",
            $"{prefix}/slot/+/state",
            $"{prefix}/device/+/status",
            $"{prefix}/resync",
            $"{prefix}/alert"
        };

        var builder = new MqttClientSubscribeOptionsBuilder();

        foreach (string topic in topics)
        {
            builder.WithTopicFilter(filter =>
            {
                filter.WithTopic(topic);
                filter.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            });
        }

        try
        {
            await _client.SubscribeAsync(builder.Build());

            _logger.LogInformation(
                "Đã subscribe thành công TẤT CẢ {Count} topic dưới prefix '{Prefix}':",
                topics.Length,
                prefix);

            foreach (string topic in topics)
            {
                _logger.LogInformation("  [SUB-OK] {Topic}", topic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi subscribe topics MQTT!");
        }
        finally
        {
            Interlocked.Exchange(ref _isSubscribing, 0);
        }
    }

    private async Task HandleMessageAsync(
        MqttApplicationMessageReceivedEventArgs args)
    {
        string topic = args.ApplicationMessage.Topic ?? "";

        string payload =
            args.ApplicationMessage.ConvertPayloadToString();

        string prefix = _settings.TopicPrefix.TrimEnd('/');

        if (!topic.StartsWith(
            prefix + "/",
            StringComparison.Ordinal))
        {
            return;
        }

        string topicName = topic[(prefix.Length + 1)..];

        if (topicName == "heartbeat")
            return;

        // Giới hạn payload nhận từ public broker
        if (payload.Length > 32768)
        {
            _logger.LogWarning(
                "Oversized MQTT payload on {Topic}",
                topicName);
            return;
        }

        // Chống nghẽn: Timeout 3 giây cho lock để không chặn luồng nhận message khi broker dồn tin
        if (!await _messageLock.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            _logger.LogWarning("Bỏ qua xử lý MQTT topic {Topic} do lock chờ quá 3s.", topicName);
            return;
        }

    try
    {
        using var scope = _scopeFactory.CreateScope();

        if (topicName.StartsWith("gate/") && topicName.EndsWith("/event"))
        {
            await HandleGateEventAsync(scope, topicName, payload);
            return;
        }

        if (topicName.StartsWith("checkpoint/") && topicName.EndsWith("/event"))
        {
            await HandleCheckpointEventAsync(scope, topicName, payload);
            return;
        }

        if (topicName.StartsWith("slot/") && topicName.EndsWith("/state"))
        {
            await HandleSlotStateAsync(scope, topicName, payload);
            return;
        }

        if (topicName.StartsWith("device/") && topicName.EndsWith("/status"))
        {
            await HandleDeviceStatusAsync(scope, topicName, payload);
            return;
        }

        if (topicName == "resync")
        {
            await HandleResyncAuditAsync(scope, payload);
            return;
        }

        if (topicName == "alert")
        {
            await HandleAlertMessageAsync(scope, payload);
            return;
        }

        _logger.LogDebug("MQTT unhandled topic: {Topic}", topicName);
    }
    catch (Exception ex)
    {
        _logger.LogError(
            ex,
            "MQTT processing failed: {Topic}",
            topicName);
    }
    finally
    {
        _messageLock.Release();
    }
}

    private async Task HandleGateEventAsync(IServiceScope scope, string topicName, string payload)
    {
        var parts = topicName.Split('/');
        if (parts.Length < 3) return;
        string gateId = parts[1].ToLowerInvariant();

        var gateService = scope.ServiceProvider.GetRequiredService<GateService>();
        long? orderId = null;
        string? eventId = null;

        try
        {
            if (payload.TrimStart().StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("eventId", out var eProp))
                    eventId = eProp.GetString();
                if (doc.RootElement.TryGetProperty("orderId", out var prop))
                    orderId = prop.GetInt64();
            }
            else if (payload.Contains("|"))
            {
                var p = payload.Split('|');
                if (p.Length > 1 && long.TryParse(p[1], out long id))
                    orderId = id;
            }
            else if (long.TryParse(payload, out long directId))
            {
                orderId = directId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể parse payload gate event: {Payload}", payload);
        }

        if (gateId == "in")
        {
            await gateService.ProcessGateInPassAsync(orderId);
            _logger.LogInformation("Gate IN pass thành công (OrderId: {OrderId}, EventId: {EventId}).", orderId?.ToString() ?? "Auto-matched", eventId ?? "N/A");
        }
        else if (gateId == "out")
        {
            await gateService.ScanGateOutAsync(new GateOutScanRequest { OrderId = orderId });
            _logger.LogInformation("Gate OUT pass thành công (OrderId: {OrderId}, EventId: {EventId}).", orderId?.ToString() ?? "Auto-matched", eventId ?? "N/A");
        }

        if (!string.IsNullOrEmpty(eventId))
        {
            await PublishEventAckAsync(eventId);
        }
    }

    private async Task HandleCheckpointEventAsync(IServiceScope scope, string topicName, string payload)
    {
        var parts = topicName.Split('/');
        if (parts.Length < 3) return;
        string checkpointId = parts[1];

        var gateService = scope.ServiceProvider.GetRequiredService<GateService>();
        long? orderId = null;
        string? eventId = null;

        try
        {
            if (payload.TrimStart().StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("eventId", out var eProp))
                    eventId = eProp.GetString();
                if (doc.RootElement.TryGetProperty("orderId", out var prop))
                    orderId = prop.GetInt64();
            }
            else if (long.TryParse(payload, out long id))
            {
                orderId = id;
            }
        }
        catch { }

        await gateService.PassCheckpointAsync(checkpointId, orderId);

        if (!string.IsNullOrEmpty(eventId))
        {
            await PublishEventAckAsync(eventId);
        }
    }

    public async Task PublishEventAckAsync(string eventId)
    {
        if (_client == null || !_client.IsConnected) return;

        try
        {
            string topic = _settings.TopicPrefix.TrimEnd('/') + "/ack";
            string json = JsonSerializer.Serialize(new { eventId });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(json)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.PublishAsync(message);
            _logger.LogInformation("Sent ACK for eventId: {EventId}", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish event ACK for {EventId}", eventId);
        }
    }

    private async Task HandleSlotStateAsync(IServiceScope scope, string topicName, string payload)
    {
        var parts = topicName.Split('/');
        if (parts.Length < 3) return;
        string slotId = parts[1].ToUpperInvariant();

        double distanceCm = -1;
        try
        {
            if (payload.TrimStart().StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("distanceCm", out var dProp))
                    distanceCm = dProp.GetDouble();
                else if (doc.RootElement.TryGetProperty("distance", out var distProp))
                    distanceCm = distProp.GetDouble();
            }
            else if (double.TryParse(payload, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedVal))
            {
                distanceCm = parsedVal;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi đọc khoảng cách từ slot state: {Payload}", payload);
            return;
        }

        if (distanceCm < 0) return;

        var db = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        var slot = await db.ViTriDoXes.FindAsync(slotId);
        if (slot == null) return;

        DateTime now = DateTime.UtcNow;
        _recentSlotReadings[slotId] = (distanceCm, now);

        string oldStatus = slot.TrangThai;
        string newStatus;

        // Đánh giá khoảng cách
        // <= 40 cm: OCCUPIED
        // 40 < d <= 80 cm: PARTIAL (kiểm tra OVERLAP ô liền kề)
        // > 80 cm: FREE
        if (distanceCm <= 40.0)
        {
            newStatus = "OCCUPIED";
        }
        else if (distanceCm <= 80.0)
        {
            string? adjacentSlotId = FindAdjacentSlotWithPartial(slotId, now, 30);
            if (adjacentSlotId != null)
            {
                newStatus = "OVERLAP";

                string prefix = _settings.TopicPrefix.TrimEnd('/');
                await PublishAsync($"{prefix}/alert", $"{{\"source\":\"BACKEND\",\"event\":\"OVERLAP\",\"slot\":\"{slotId}\",\"adjacent\":\"{adjacentSlotId}\",\"distanceCm\":{distanceCm}}}");
                await PublishAsync($"{prefix}/slot/{slotId}/cmd", "YELLOW");
                await PublishAsync($"{prefix}/slot/{adjacentSlotId}/cmd", "YELLOW");

                var alertNotifier = scope.ServiceProvider.GetService<IParkingNotificationService>();
                if (alertNotifier != null)
                {
                    await alertNotifier.NotifyAlertAsync("OVERLAP", $"Phát hiện xe đỗ lấn vạch giữa ô {slotId} và {adjacentSlotId} (cự ly: {distanceCm:F1} cm)");
                }

                db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    LoaiSuKien = "OVERLAP_WARNING",
                    NguonSuKien = "ESP32",
                    MucDo = "WARNING",
                    NoiDung = $"Phát hiện đỗ lấn vạch (OVERLAP) tại ô {slotId} (cự ly {distanceCm:F1}cm) liên đới ô liền kề {adjacentSlotId}."
                });
            }
            else
            {
                newStatus = "PARTIAL";
            }
        }
        else
        {
            if (slot.TrangThai == "GUIDING" || slot.TrangThai == "BOOKED" || slot.TrangThai == "HELD")
            {
                newStatus = slot.TrangThai;
            }
            else
            {
                newStatus = "FREE";
            }
        }

        slot.KhoangCachCm = (decimal)distanceCm;
        slot.TrangThai = newStatus;
        slot.LanCapNhat = now;

        if (oldStatus != newStatus)
        {
            db.LichSuCamBiens.Add(new LichSuCamBien
            {
                MaViTri = slot.MaViTri,
                KhoangCachCm = slot.KhoangCachCm,
                TrangThaiCu = oldStatus,
                TrangThaiMoi = newStatus,
                ThoiGianGhiNhan = now
            });
        }

        if (newStatus == "OCCUPIED")
        {
            var activeOrder = await db.DonDatChos
                .Include(d => d.DonDatChoViTris)
                .Where(d => (d.TrangThai == "ENTERED" || d.TrangThai == "PAID") && d.DonDatChoViTris.Any(v => v.MaViTri == slotId))
                .FirstOrDefaultAsync();

            if (activeOrder != null)
            {
                activeOrder.TrangThai = "PARKED";
                foreach (var detail in activeOrder.DonDatChoViTris.Where(v => v.MaViTri == slotId))
                {
                    detail.TrangThai = "OCCUPIED";
                    detail.ThoiGianVaoO = now;
                }

                db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    MaDon = activeOrder.MaDon,
                    LoaiSuKien = "VEHICLE_PARKED",
                    NguonSuKien = "ESP32",
                    MucDo = "INFO",
                    NoiDung = $"Xe đã đỗ an toàn vào ô {slotId}. Đơn chuyển sang PARKED."
                });

                string prefix = _settings.TopicPrefix.TrimEnd('/');
                await PublishAsync($"{prefix}/slot/{slotId}/cmd", "RED");

                // Khi xe đỗ thành công -> Số xe tìm chỗ giảm -> Tự động mở lại cổng vào nếu đang HOLD
                int searchingCount = await db.DonDatChos.CountAsync(d => d.TrangThai == "ENTERED" && d.MaDon != activeOrder.MaDon);
                if (searchingCount < 3)
                {
                    await PublishAsync($"{prefix}/gate/in/cmd", "{\"cmd\":\"OPEN\",\"reason\":\"UNHOLD\",\"message\":\"Bãi xe đã thông thoáng\"}");
                }
            }
        }

        await db.SaveChangesAsync();

        var notifier = scope.ServiceProvider.GetService<IParkingNotificationService>();
        if (notifier != null)
        {
            await notifier.NotifySlotChangedAsync(slot.MaViTri, newStatus, (decimal)distanceCm);
            await notifier.NotifySlotsReloadAsync();
        }

        await PublishSummaryAsync(db);
    }

    public static async Task<int> RestoreAndRecalculateOverlapFromDbAsync(
        SmartParkingDbContext db,
        IMqttPublisher? mqtt = null,
        IParkingNotificationService? notifier = null,
        string prefix = "dacn-smartparking/lot1")
    {
        DateTime now = DateTime.UtcNow;
        var slots = await db.ViTriDoXes
            .Where(s => s.KhoangCachCm != null)
            .ToListAsync();

        int overlapCount = 0;
        foreach (var slot in slots)
        {
            double dist = (double)slot.KhoangCachCm!.Value;
            _recentSlotReadings[slot.MaViTri] = (dist, now);
        }

        var partialSlots = slots.Where(s => (double)s.KhoangCachCm!.Value > 40.0 && (double)s.KhoangCachCm!.Value <= 80.0).ToList();

        foreach (var slot in partialSlots)
        {
            string? adjId = FindAdjacentSlotWithPartial(slot.MaViTri, now, 30);
            if (adjId != null)
            {
                var adjSlot = slots.FirstOrDefault(s => s.MaViTri == adjId);
                if (adjSlot != null && slot.TrangThai != "OVERLAP")
                {
                    slot.TrangThai = "OVERLAP";
                    adjSlot.TrangThai = "OVERLAP";
                    slot.LanCapNhat = now;
                    adjSlot.LanCapNhat = now;
                    overlapCount++;

                    if (mqtt != null)
                    {
                        string p = prefix.TrimEnd('/');
                        await mqtt.PublishAsync($"{p}/alert", $"{{\"source\":\"BACKEND\",\"event\":\"OVERLAP\",\"slot\":\"{slot.MaViTri}\",\"adjacent\":\"{adjId}\",\"distanceCm\":{slot.KhoangCachCm}}}", retain: false);
                        await mqtt.PublishAsync($"{p}/slot/{slot.MaViTri}/cmd", "YELLOW", retain: false);
                        await mqtt.PublishAsync($"{p}/slot/{adjId}/cmd", "YELLOW", retain: false);
                    }

                    if (notifier != null)
                    {
                        await notifier.NotifyAlertAsync("OVERLAP", $"Phục hồi: Phát hiện đỗ lấn vạch giữa ô {slot.MaViTri} và {adjId}");
                    }
                }
            }
        }

        if (overlapCount > 0)
        {
            await db.SaveChangesAsync();
            if (notifier != null)
            {
                await notifier.NotifySlotsReloadAsync();
            }
        }

        return overlapCount;
    }

    public async Task PublishSummaryAsync(SmartParkingDbContext db)
    {
        try
        {
            var counts = await db.ViTriDoXes
                .GroupBy(s => s.TrangThai)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            int total = counts.Sum(c => c.Count);
            int free = counts.FirstOrDefault(c => c.Status == "FREE")?.Count ?? 0;
            int occupied = counts.FirstOrDefault(c => c.Status == "OCCUPIED")?.Count ?? 0;
            int overlap = counts.FirstOrDefault(c => c.Status == "OVERLAP")?.Count ?? 0;

            string prefix = _settings.TopicPrefix.TrimEnd('/');
            string summaryJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                total,
                free,
                occupied,
                overlap
            });

            await PublishAsync($"{prefix}/summary", summaryJson, retain: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lỗi publish MQTT summary");
        }
    }

    private static string? FindAdjacentSlotWithPartial(string slotId, DateTime now, int windowSec)
    {
        if (slotId.Length < 2) return null;
        char row = slotId[0];
        if (!int.TryParse(slotId[1..], out int col)) return null;

        int[] adjacentCols = { col - 1, col + 1 };
        foreach (int adjCol in adjacentCols)
        {
            if (adjCol < 1 || adjCol > 8) continue;
            string adjId = $"{row}{adjCol}";
            if (_recentSlotReadings.TryGetValue(adjId, out var reading))
            {
                if (reading.DistanceCm <= 80.0 && (now - reading.Timestamp).TotalSeconds <= windowSec)
                {
                    return adjId;
                }
            }
        }
        return null;
    }

    private async Task HandleDeviceStatusAsync(IServiceScope scope, string topicName, string payload)
    {
        var parts = topicName.Split('/');
        if (parts.Length < 3) return;
        string deviceId = parts[1];
        string status = payload.Trim().ToUpperInvariant();

        var db = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        DateTime now = DateTime.UtcNow;

        // Cập nhật trạng thái thiết bị trong bảng ThietBi (nếu có)
        try
        {
            var dev = await db.ThietBis.FindAsync(deviceId);
            if (dev != null)
            {
                dev.TrangThai = status;
                dev.LanCapNhat = now;
            }
        }
        catch { /* Fallback nếu bảng chưa tạo */ }

        // Xác định loại thiết bị và hàng ô tương ứng
        string? targetRow = null;
        if (deviceId.StartsWith("esp32_row_"))
        {
            targetRow = deviceId["esp32_row_".Length..].ToUpperInvariant();
        }
        else if (deviceId == "esp32_lot1")
        {
            targetRow = "E";
        }

        if (status == "OFFLINE")
        {
            if (!string.IsNullOrEmpty(targetRow))
            {
                var affectedSlots = await db.ViTriDoXes.Where(s => s.Hang == targetRow).ToListAsync();
                foreach (var slot in affectedSlots)
                {
                    slot.TrangThai = "UNKNOWN";
                    slot.LanCapNhat = now;
                }
                _logger.LogWarning("Device {DeviceId} OFFLINE LWT trigger. Marked row {Row} slots as UNKNOWN.", deviceId, targetRow);
            }

            db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                LoaiSuKien = "DEVICE_OFFLINE_LWT",
                NguonSuKien = "ESP32",
                MucDo = "ERROR",
                NoiDung = $"Mất kết nối thiết bị {deviceId} (LWT OFFLINE). {(targetRow != null ? $"Các ô đỗ hàng {targetRow} đã chuyển sang UNKNOWN." : "Thiết bị cổng đã ngắt kết nối.")}"
            });

            await db.SaveChangesAsync();

            string prefix = _settings.TopicPrefix.TrimEnd('/');
            await PublishAsync($"{prefix}/alert", $"{{\"source\":\"BACKEND\",\"event\":\"DEVICE_OFFLINE\",\"deviceId\":\"{deviceId}\",\"message\":\"LWT trigger: Device {deviceId} disconnected\"}}");

            var notifier = scope.ServiceProvider.GetService<IParkingNotificationService>();
            if (notifier != null)
            {
                await notifier.NotifyAlertAsync("DEVICE_OFFLINE", $"Thiết bị {deviceId} mất kết nối (LWT)");
                await notifier.NotifySlotsReloadAsync();
            }
        }
        else if (status == "ONLINE")
        {
            db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                LoaiSuKien = "DEVICE_ONLINE",
                NguonSuKien = "ESP32",
                MucDo = "INFO",
                NoiDung = $"Thiết bị {deviceId} đã kết nối lại (ONLINE)."
            });
            await db.SaveChangesAsync();
            _logger.LogInformation("Device {DeviceId} is ONLINE.", deviceId);

            if (!string.IsNullOrEmpty(targetRow))
            {
                await HandleResyncAuditAsync(scope, targetRow);
            }
        }
    }

    private async Task HandleResyncAuditAsync(IServiceScope scope, string targetRowOrPayload)
    {
        var db = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        DateTime now = DateTime.UtcNow;

        string? filterRow = null;
        if (!string.IsNullOrEmpty(targetRowOrPayload) && targetRowOrPayload.Length <= 2)
        {
            filterRow = targetRowOrPayload.ToUpperInvariant();
        }
        else if (!string.IsNullOrEmpty(targetRowOrPayload) && targetRowOrPayload.StartsWith("esp32_row_"))
        {
            filterRow = targetRowOrPayload["esp32_row_".Length..].ToUpperInvariant();
        }

        var slotsQuery = db.ViTriDoXes.AsQueryable();
        if (!string.IsNullOrEmpty(filterRow))
        {
            slotsQuery = slotsQuery.Where(s => s.Hang == filterRow);
        }
        var targetSlots = await slotsQuery.ToListAsync();

        foreach (var slot in targetSlots)
        {
            bool hasValidBookingOrSession = await db.DonDatChoViTris
                .AnyAsync(v => v.MaViTri == slot.MaViTri &&
                               (v.DonDatCho.TrangThai == "ENTERED" ||
                                v.DonDatCho.TrangThai == "PARKED" ||
                                v.DonDatCho.TrangThai == "GUIDING"));

            if ((slot.KhoangCachCm <= 40m || slot.TrangThai == "OCCUPIED") && !hasValidBookingOrSession)
            {
                db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    LoaiSuKien = "UNAUTHORIZED_PARKING",
                    NguonSuKien = "BACKEND_AUDIT",
                    MucDo = "WARNING",
                    NoiDung = $"Cảnh báo: Phát hiện xe đỗ chui / trái phép tại ô {slot.MaViTri} (Cảm biến báo có xe nhưng không có đơn đặt chỗ hợp lệ)."
                });

                string prefix = _settings.TopicPrefix.TrimEnd('/');
                await PublishAsync($"{prefix}/alert", $"{{\"source\":\"BACKEND\",\"event\":\"UNAUTHORIZED_PARKING\",\"slot\":\"{slot.MaViTri}\",\"message\":\"Xe đỗ chui tại ô {slot.MaViTri}\"}}");
                _logger.LogWarning("Phát hiện xe đỗ chui tại ô {SlotId} qua Resync Audit.", slot.MaViTri);

                var notifier = scope.ServiceProvider.GetService<IParkingNotificationService>();
                if (notifier != null)
                {
                    await notifier.NotifyAlertAsync("UNAUTHORIZED_PARKING", $"Phát hiện xe đỗ chui tại ô {slot.MaViTri}");
                }
            }
            else if (slot.TrangThai == "UNKNOWN")
            {
                slot.TrangThai = hasValidBookingOrSession ? "OCCUPIED" : "FREE";
                slot.LanCapNhat = now;
            }

            // Gửi lại màu đèn LED hiện tại cho thiết bị (không dùng retain)
            string ledCmd = slot.TrangThai switch
            {
                "OCCUPIED" => "RED",
                "OVERLAP" => "YELLOW",
                "GUIDING" => "BLUE",
                _ => "GREEN"
            };
            string pfx = _settings.TopicPrefix.TrimEnd('/');
            await PublishAsync($"{pfx}/slot/{slot.MaViTri}/cmd", ledCmd, retain: false);
        }

        await db.SaveChangesAsync();

        var finalNotifier = scope.ServiceProvider.GetService<IParkingNotificationService>();
        if (finalNotifier != null)
        {
            await finalNotifier.NotifySlotsReloadAsync();
        }

        // Khôi phục RAM readings và tính toán lại OVERLAP, sau đó đẩy summary
        await RestoreAndRecalculateOverlapFromDbAsync(db, this, finalNotifier, _settings.TopicPrefix);
        await PublishSummaryAsync(db);
    }

    private async Task HandleAlertMessageAsync(IServiceScope scope, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // Nếu cảnh báo do chính Backend phát đi (mang cờ "source": "BACKEND"), bỏ qua để tránh ghi đè / trùng lặp
            if (root.TryGetProperty("source", out var srcElem) &&
                string.Equals(srcElem.GetString(), "BACKEND", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Đọc loại sự kiện (type hoặc event)
            string alertType = "ALERT";
            if (root.TryGetProperty("type", out var typeElem))
            {
                alertType = typeElem.GetString() ?? "ALERT";
            }
            else if (root.TryGetProperty("event", out var eventElem))
            {
                alertType = eventElem.GetString() ?? "ALERT";
            }

            // Đọc nội dung thông điệp
            string message = "Cảnh báo từ thiết bị";
            if (root.TryGetProperty("message", out var msgElem))
            {
                message = msgElem.GetString() ?? message;
            }
            else if (alertType == "UNAUTHORIZED_ENTRY")
            {
                message = "Phát hiện xe tiếp cận barie khi cổng vào đang đóng!";
            }
            else if (root.TryGetProperty("slot", out var slotElem))
            {
                message = $"Cảnh báo {alertType} tại vị trí {slotElem.GetString()}";
            }

            // 1. Ghi nhận vào bảng NhatKyHeThong trên SQL Server
            var db = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
            var log = new NhatKyHeThong
            {
                LoaiSuKien = alertType,
                NguonSuKien = "ESP32",
                MucDo = "WARNING",
                NoiDung = message,
                Mqtttopic = $"{_settings.TopicPrefix.TrimEnd('/')}/alert",
                ThoiGianGhiNhan = DateTime.UtcNow
            };
            db.NhatKyHeThongs.Add(log);
            await db.SaveChangesAsync();

            _logger.LogWarning("Đã lưu NhatKyHeThong cảnh báo từ ESP32: {Type} - {Message}", alertType, message);

            // 2. Đẩy thông báo thời gian thực lên SignalR cho admin.html và staff.html
            var notifier = scope.ServiceProvider.GetService<IParkingNotificationService>();
            if (notifier != null)
            {
                await notifier.NotifyAlertAsync(alertType, message, new { source = "ESP32", payload });
                await notifier.NotifySystemLogAsync(alertType, "WARNING", message, "ESP32");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xử lý alert từ MQTT payload: {Payload}", payload);
        }
    }
}
