using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Dtos;
using SmartParking.Api.Models;
using SmartParking.Api.Services;

namespace SmartParking.Api.Controllers;

public class DemoTimeAdvanceRequest
{
    public int Minutes { get; set; } = 15;
}

public class DemoSlotStateRequest
{
    public string SlotId { get; set; } = string.Empty;
    public string Action { get; set; } = "PARK"; // PARK, OVERLAP, LEAVE, MAINTENANCE, FREE
    public double? DistanceCm { get; set; }
}

public class DemoPowerRequest
{
    public string DeviceId { get; set; } = "esp32_row_e";
    public string State { get; set; } = "OFFLINE"; // OFFLINE, ONLINE
}

public class DemoGatePassRequest
{
    public string Gate { get; set; } = "IN"; // IN, OUT
    public long OrderId { get; set; }
}

public class DemoCheckpointRequest
{
    public string CheckpointId { get; set; } = "CP1";
    public long? OrderId { get; set; }
}

public class BarrierControlRequest
{
    public string Gate { get; set; } = "IN"; // IN, OUT
    public string Action { get; set; } = "OPEN"; // OPEN, CLOSE
}

[ApiController]
[Route("api/demo")]
[AllowAnonymous]
public class DemoController : ControllerBase
{
    private readonly SmartParkingDbContext _db;
    private readonly ScaledTimeProvider _timeProvider;
    private readonly ParkingTimeoutWorker _timeoutWorker;
    private readonly IOptionsMonitor<ParkingPolicySettings> _policyOptions;
    private readonly IParkingNotificationService _notifier;
    private readonly IMqttPublisher _mqtt;
    private readonly GateService _gateService;
    private readonly ParkingOrderService _orderService;
    private readonly ILogger<DemoController> _logger;

    public DemoController(
        SmartParkingDbContext db,
        ScaledTimeProvider timeProvider,
        ParkingTimeoutWorker timeoutWorker,
        IOptionsMonitor<ParkingPolicySettings> policyOptions,
        IParkingNotificationService notifier,
        IMqttPublisher mqtt,
        GateService gateService,
        ParkingOrderService orderService,
        ILogger<DemoController> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _timeoutWorker = timeoutWorker;
        _policyOptions = policyOptions;
        _notifier = notifier;
        _mqtt = mqtt;
        _gateService = gateService;
        _orderService = orderService;
        _logger = logger;
    }

    private void CheckDemoMode()
    {
        if (!_policyOptions.CurrentValue.DemoMode)
        {
            throw new BadHttpRequestException("Chế độ DemoMode đang tắt trong hệ thống.");
        }
    }

    /// <summary>
    /// Kiểm tra trạng thái DemoMode và thời gian hệ thống hiện tại
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        bool demoMode = _policyOptions.CurrentValue.DemoMode;
        var nowUtc = _timeProvider.GetUtcNow();
        var nowVn = nowUtc.ToOffset(TimeSpan.FromHours(7));

        return Ok(new
        {
            demoMode,
            currentTimeUtc = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            currentTimeLocal = nowVn.ToString("yyyy-MM-ddTHH:mm:ss+07:00"),
            displayTime = nowVn.ToString("HH:mm:ss dd/MM/yyyy"),
            timeScale = _timeProvider.TimeScale,
            offsetMinutes = _timeProvider.ManualOffset.TotalMinutes
        });
    }

    /// <summary>
    /// Tua nhanh thời gian hệ thống (+5, +15, +30, +60 phút) và kích hoạt worker xử lý ngay
    /// </summary>
    [HttpPost("time/advance")]
    public async Task<IActionResult> AdvanceTime([FromBody] DemoTimeAdvanceRequest request)
    {
        CheckDemoMode();

        int mins = request.Minutes > 0 ? request.Minutes : 15;
        _timeProvider.Advance(TimeSpan.FromMinutes(mins));

        var nowUtc = _timeProvider.GetUtcNow();
        var nowVn = nowUtc.ToOffset(TimeSpan.FromHours(7));

        // Kích hoạt ngay background worker để xử lý hết hạn đơn hoặc đổi ô nếu có
        await _timeoutWorker.ProcessTimeoutsAsync();

        await _notifier.NotifyTimeChangedAsync(nowUtc, _timeProvider.ManualOffset);
        await _notifier.NotifySlotsReloadAsync();
        await _notifier.NotifySystemLogAsync("TIME_TRAVEL", "INFO", $"Đã tua nhanh thời gian thêm +{mins} phút (Giờ mới: {nowVn:HH:mm:ss})", "DEMO");

        return Ok(new
        {
            message = $"Đã tua thời gian thêm +{mins} phút.",
            advancedMinutes = mins,
            totalOffsetMinutes = _timeProvider.ManualOffset.TotalMinutes,
            currentTimeLocal = nowVn.ToString("yyyy-MM-ddTHH:mm:ss+07:00"),
            displayTime = nowVn.ToString("HH:mm:ss dd/MM/yyyy")
        });
    }

    /// <summary>
    /// Đặt lại thời gian về đồng hồ thật
    /// </summary>
    [HttpPost("time/reset")]
    public async Task<IActionResult> ResetTime()
    {
        CheckDemoMode();

        _timeProvider.ResetOffset();
        var nowUtc = _timeProvider.GetUtcNow();
        var nowVn = nowUtc.ToOffset(TimeSpan.FromHours(7));

        await _timeoutWorker.ProcessTimeoutsAsync();
        await _notifier.NotifyTimeChangedAsync(nowUtc, _timeProvider.ManualOffset);
        await _notifier.NotifySlotsReloadAsync();
        await _notifier.NotifySystemLogAsync("TIME_RESET", "INFO", "Đã đặt lại đồng hồ về thời gian thực tế", "DEMO");

        return Ok(new
        {
            message = "Đã đặt lại thời gian về thời gian thực tế.",
            currentTimeLocal = nowVn.ToString("yyyy-MM-ddTHH:mm:ss+07:00"),
            displayTime = nowVn.ToString("HH:mm:ss dd/MM/yyyy")
        });
    }

    /// <summary>
    /// Đặt lại toàn bộ dữ liệu demo: Xóa đơn hàng, phiên gửi xe, nhật ký; đưa 40 ô về FREE
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetDemoData()
    {
        CheckDemoMode();

        try
        {
            // Xóa dữ liệu giao dịch
            _db.NhatKyQuetMas.RemoveRange(_db.NhatKyQuetMas);
            _db.LichSuCamBiens.RemoveRange(_db.LichSuCamBiens);
            _db.MaVaoCongs.RemoveRange(_db.MaVaoCongs);
            _db.MaTienMats.RemoveRange(_db.MaTienMats);
            _db.ThanhToans.RemoveRange(_db.ThanhToans);
            _db.DonDatChoViTris.RemoveRange(_db.DonDatChoViTris);
            _db.DonDatChos.RemoveRange(_db.DonDatChos);
            _db.NhatKyHeThongs.RemoveRange(_db.NhatKyHeThongs);

            await _db.SaveChangesAsync();

            // Đưa tất cả 40 ô về FREE
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var slots = await _db.ViTriDoXes.ToListAsync();
            foreach (var s in slots)
            {
                s.TrangThai = "FREE";
                s.KhoangCachCm = 200m;
                s.LanCapNhat = now;
            }

            _timeProvider.ResetOffset();

            // Đồng bộ trạng thái đèn xanh cho cả 8 ô vật lý E1-E8 trên MQTT (xóa sạch cờ RED retained cũ trên broker)
            string prefix = _policyOptions.CurrentValue.MqttTopicPrefix ?? "dacn-smartparking/lot1";
            for (int i = 1; i <= 8; i++)
            {
                await _mqtt.PublishAsync($"{prefix}/slot/E{i}/cmd", "GREEN", retain: false);
            }

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                LoaiSuKien = "DEMO_RESET",
                NguonSuKien = "DEMO",
                MucDo = "INFO",
                NoiDung = "Đã đặt lại toàn bộ dữ liệu demo về trạng thái ban đầu (40 ô trống, đã xóa toàn bộ đơn)."
            });

            await _db.SaveChangesAsync();

            await _notifier.NotifySlotsReloadAsync();
            await _notifier.NotifyTimeChangedAsync(_timeProvider.GetUtcNow(), TimeSpan.Zero);
            await _notifier.NotifySystemLogAsync("DEMO_RESET", "INFO", "Đã đặt lại dữ liệu demo thành công.", "DEMO");

            return Ok(new
            {
                success = true,
                message = "Đã đặt lại dữ liệu demo thành công. Toàn bộ 40 ô đỗ xe đã về FREE."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi reset demo data.");
            return StatusCode(500, new { message = "Lỗi khi reset dữ liệu demo: " + ex.Message });
        }
    }

    /// <summary>
    /// Mô phỏng thay đổi trạng thái ô đỗ (áp dụng cho cả 32 ô ảo A-D và 8 ô vật lý E).
    /// Toàn bộ đi qua cùng hàm xử lý trạng thái thống nhất ProcessSlotTelemetryAsync như khi nhận tin nhắn MQTT từ cảm biến thật.
    /// </summary>
    [HttpPost("slot/state")]
    public async Task<IActionResult> ChangeSlotState([FromBody] DemoSlotStateRequest request)
    {
        CheckDemoMode();

        string slotId = (request.SlotId ?? "").Trim().ToUpperInvariant();
        var slot = await _db.ViTriDoXes.FindAsync(slotId);
        if (slot == null)
        {
            return NotFound(new { message = $"Không tìm thấy ô đỗ {slotId}." });
        }

        string action = (request.Action ?? "PARK").Trim().ToUpperInvariant();

        if (action == "MAINTENANCE")
        {
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            string oldStatus = slot.TrangThai;
            slot.TrangThai = "MAINTENANCE";
            slot.KhoangCachCm = 0m;
            slot.LanCapNhat = now;
            await _db.SaveChangesAsync();

            await _notifier.NotifySlotChangedAsync(slot.MaViTri, "MAINTENANCE", 0m);
            await _notifier.NotifySlotsReloadAsync();
            return Ok(new
            {
                success = true,
                slotId = slot.MaViTri,
                oldStatus,
                newStatus = "MAINTENANCE",
                distanceCm = 0m
            });
        }

        double distance = action switch
        {
            "PARK" or "CAR_ENTER" => 30.0,
            "OVERLAP" => request.DistanceCm.HasValue && request.DistanceCm.Value > 0 ? request.DistanceCm.Value : 60.0,
            "LEAVE" or "FREE" => 150.0,
            _ => 150.0
        };

        var updatedSlot = await _gateService.ProcessSlotTelemetryAsync(slotId, distance, "DEMO");

        return Ok(new
        {
            success = true,
            slotId = updatedSlot.MaViTri,
            newStatus = updatedSlot.TrangThai,
            distanceCm = updatedSlot.KhoangCachCm
        });
    }

    /// <summary>
    /// Mô phỏng sự kiện "Xe vào ô" đối với ô ảo A-D (hoặc ô bất kỳ đang ở trạng thái GUIDING)
    /// </summary>
    [HttpPost("virtual-slot/car-enter")]
    public async Task<IActionResult> VirtualSlotCarEnter([FromBody] DemoSlotStateRequest request)
    {
        CheckDemoMode();
        string slotId = (request.SlotId ?? "").Trim().ToUpperInvariant();
        var updatedSlot = await _gateService.ProcessSlotTelemetryAsync(slotId, 30.0, "DEMO_VIRTUAL");

        return Ok(new
        {
            success = true,
            message = $"Đã ghi nhận xe vào ô {slotId}.",
            slotId = updatedSlot.MaViTri,
            status = updatedSlot.TrangThai
        });
    }

    /// <summary>
    /// Mô phỏng cắt điện / có điện lại cho từng board thiết bị (MQTT LWT & Resync Audit)
    /// </summary>
    [HttpPost("power")]
    public async Task<IActionResult> TogglePower([FromBody] DemoPowerRequest request)
    {
        CheckDemoMode();

        string deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? "esp32_row_e" : request.DeviceId.Trim();
        string state = (request.State ?? "OFFLINE").Trim().ToUpperInvariant();
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        string? targetRow = null;
        if (deviceId.StartsWith("esp32_row_"))
        {
            targetRow = deviceId["esp32_row_".Length..].ToUpperInvariant();
        }

        if (state == "OFFLINE")
        {
            if (!string.IsNullOrEmpty(targetRow))
            {
                var affectedSlots = await _db.ViTriDoXes.Where(s => s.Hang == targetRow).ToListAsync();
                foreach (var slot in affectedSlots)
                {
                    slot.TrangThai = "UNKNOWN";
                    slot.LanCapNhat = now;
                }
            }

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                LoaiSuKien = "DEVICE_OFFLINE_LWT",
                NguonSuKien = "DEMO",
                MucDo = "ERROR",
                NoiDung = $"Cắt điện thiết bị {deviceId} (Mô phỏng MQTT LWT). {(targetRow != null ? $"Toàn bộ ô hàng {targetRow} chuyển sang UNKNOWN." : "Board cổng kiểm soát ngắt kết nối.")}"
            });

            await _db.SaveChangesAsync();

            await _notifier.NotifyAlertAsync("DEVICE_OFFLINE", $"MẤT KẾT NỐI THIẾT BỊ {deviceId.ToUpper()}: {(targetRow != null ? $"Hàng {targetRow} đã chuyển sang UNKNOWN." : "Cổng kiểm soát đã tắt.")}");
            await _notifier.NotifySlotsReloadAsync();
            await _notifier.NotifySystemLogAsync("DEVICE_OFFLINE_LWT", "ERROR", $"Mất điện thiết bị {deviceId}.", "DEMO");

            return Ok(new { success = true, deviceId, state = "OFFLINE", message = $"Đã ngắt điện {deviceId}." });
        }
        else
        {
            // Có điện lại: Audit và phục hồi
            int unauthorizedCount = 0;
            if (!string.IsNullOrEmpty(targetRow))
            {
                var targetSlots = await _db.ViTriDoXes.Where(s => s.Hang == targetRow).ToListAsync();
                foreach (var slot in targetSlots)
                {
                    bool hasActiveOrder = await _db.DonDatChoViTris
                        .AnyAsync(v => v.MaViTri == slot.MaViTri &&
                                       (v.DonDatCho.TrangThai == "ENTERED" ||
                                        v.DonDatCho.TrangThai == "PARKED" ||
                                        v.DonDatCho.TrangThai == "GUIDING"));

                    if (slot.KhoangCachCm <= 40m && !hasActiveOrder)
                    {
                        unauthorizedCount++;
                        slot.TrangThai = "OCCUPIED";
                        slot.LanCapNhat = now;
                        _db.NhatKyHeThongs.Add(new NhatKyHeThong
                        {
                            LoaiSuKien = "UNAUTHORIZED_PARKING",
                            NguonSuKien = "AUDIT",
                            MucDo = "WARNING",
                            NoiDung = $"Phát hiện xe đỗ chui tại ô {slot.MaViTri} sau khi có điện lại."
                        });
                    }
                    else if (slot.TrangThai == "UNKNOWN")
                    {
                        slot.TrangThai = hasActiveOrder ? "OCCUPIED" : "FREE";
                        slot.LanCapNhat = now;
                    }
                }
            }

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                LoaiSuKien = "DEVICE_ONLINE",
                NguonSuKien = "DEMO",
                MucDo = "INFO",
                NoiDung = $"Thiết bị {deviceId} đã có điện lại (ONLINE). Đã thực hiện đối soát phần cứng vs CSDL."
            });

            await _db.SaveChangesAsync();

            if (unauthorizedCount > 0)
            {
                await _notifier.NotifyAlertAsync("UNAUTHORIZED_PARKING", $"CẢNH BÁO AUDIT: Phát hiện {unauthorizedCount} xe đỗ chui tại {deviceId}!");
            }

            await _notifier.NotifySlotsReloadAsync();
            await _notifier.NotifySystemLogAsync("DEVICE_ONLINE", "INFO", $"{deviceId} đã online lại. Hoàn tất đối soát ({unauthorizedCount} xe đỗ chui).", "DEMO");

            return Ok(new
            {
                success = true,
                deviceId,
                state = "ONLINE",
                unauthorizedVehicles = unauthorizedCount,
                message = $"Đã cấp điện lại thành công cho {deviceId}. Hệ thống đã tự động audit đối soát cảm biến."
            });
        }
    }

    /// <summary>
    /// Đồng bộ lại toàn bộ các board (Resync all) và làm mới sơ đồ ô
    /// </summary>
    [HttpPost("resync-all")]
    public async Task<IActionResult> ResyncAll()
    {
        CheckDemoMode();

        string prefix = (_policyOptions.CurrentValue.MqttTopicPrefix ?? "dacn-smartparking/lot1").TrimEnd('/');

        // 1. Gửi lệnh resync broadcast
        await _mqtt.PublishAsync($"{prefix}/resync", "{\"action\":\"REQUEST_RESYNC\"}");

        // 2. Gửi lệnh resync tới từng board phần cứng
        string[] deviceIds = { "esp32_gate", "esp32_row_e", "esp32_row_a", "esp32_row_b", "esp32_row_c", "esp32_row_d" };
        foreach (var devId in deviceIds)
        {
            await _mqtt.PublishAsync($"{prefix}/device/{devId}/cmd", "{\"cmd\":\"RESYNC\"}");
        }

        // 3. Thông báo SignalR cập nhật lại sơ đồ ô
        await _notifier.NotifySlotsReloadAsync();
        await _notifier.NotifySystemLogAsync("MANUAL_RESYNC_ALL", "INFO", "Người dùng kích hoạt đồng bộ lại toàn bộ các board từ Demo.", "DEMO");

        return Ok(new
        {
            success = true,
            message = "Đã phát lệnh đồng bộ lại (Resync) tới tất cả các board phần cứng và làm mới sơ đồ ô."
        });
    }

    /// <summary>
    /// Mô phỏng xe đi qua cổng vào (PASS) hoặc cổng ra (LEAVE)
    /// </summary>
    [HttpPost("gate/pass")]
    public async Task<IActionResult> GatePass([FromBody] DemoGatePassRequest request)
    {
        CheckDemoMode();

        string gate = (request.Gate ?? "IN").Trim().ToUpperInvariant();
        long orderId = request.OrderId;

        if (orderId <= 0)
        {
            // Lấy đơn gần nhất phù hợp
            var candidateOrder = await _db.DonDatChos
                .Where(d => gate == "IN" ? (d.TrangThai == "PAID") : (d.TrangThai == "ENTERED" || d.TrangThai == "PARKED"))
                .OrderByDescending(d => d.ThoiGianDat)
                .FirstOrDefaultAsync();

            if (candidateOrder == null)
            {
                return BadRequest(new { message = $"Không tìm thấy đơn hàng phù hợp để mô phỏng qua cổng {gate}." });
            }

            orderId = candidateOrder.MaDon;
        }

        if (gate == "IN")
        {
            var res = await _gateService.ProcessGateInPassAsync(orderId);
            await _notifier.NotifySlotsReloadAsync();
            await _notifier.NotifySystemLogAsync("GATE_IN_PASS", "INFO", $"Xe qua cổng vào thành công cho đơn {orderId}. Bắt đầu tính giờ vào ô.", "DEMO");
            return Ok(new { success = true, gate = "IN", orderId, result = res });
        }
        else
        {
            var res = await _gateService.ScanGateOutAsync(new GateOutScanRequest { OrderId = orderId });
            await _notifier.NotifySlotsReloadAsync();
            await _notifier.NotifySystemLogAsync("GATE_OUT_PASS", "INFO", $"Xe qua cổng ra cho đơn {orderId} (Trạng thái: {res.Status}).", "DEMO");
            return Ok(new { success = true, gate = "OUT", orderId, result = res });
        }
    }

    /// <summary>
    /// Mô phỏng xe qua trạm kiểm soát (Checkpoint)
    /// </summary>
    [HttpPost("checkpoint/pass")]
    public async Task<IActionResult> CheckpointPass([FromBody] DemoCheckpointRequest request)
    {
        CheckDemoMode();

        string cpId = string.IsNullOrWhiteSpace(request.CheckpointId) ? "CP1" : request.CheckpointId.Trim().ToUpperInvariant();
        await _gateService.PassCheckpointAsync(cpId, request.OrderId);
        await _notifier.NotifySystemLogAsync("CHECKPOINT_PASS", "INFO", $"Xe qua trạm kiểm soát {cpId}. Đã tự động gia hạn thêm 5 phút.", "DEMO");

        return Ok(new { success = true, checkpointId = cpId, message = $"Xe qua trạm {cpId} thành công." });
    }

    /// <summary>
    /// Mô phỏng quét cổng vào dành cho màn hình Demo (chỉ hoạt động khi DemoMode = true)
    /// </summary>
    [HttpPost("gate/scan-in")]
    public async Task<IActionResult> DemoScanGateIn([FromBody] GateInScanRequest request)
    {
        CheckDemoMode();
        try
        {
            var res = await _gateService.ScanGateInAsync(request);
            return Ok(res);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    /// <summary>
    /// Mô phỏng quét cổng ra dành cho màn hình Demo (chỉ hoạt động khi DemoMode = true)
    /// </summary>
    [HttpPost("gate/scan-out")]
    public async Task<IActionResult> DemoScanGateOut([FromBody] GateOutScanRequest request)
    {
        CheckDemoMode();
        try
        {
            var res = await _gateService.ScanGateOutAsync(request);
            return Ok(res);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Chạy tự động 1 trong 12 kịch bản kiểm thử nghiệp vụ và trả về kết quả Mong đợi vs Thực tế
    /// </summary>
    [HttpPost("scenarios/{id:int}/run")]
    public async Task<IActionResult> RunScenario(int id)
    {
        CheckDemoMode();
        try
        {
            var result = await ExecuteScenarioAsync(id);
            await _notifier.NotifySlotsReloadAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi chạy kịch bản {ScenarioId}", id);
            return BadRequest(new { success = false, scenarioId = id, error = ex.Message });
        }
    }

    public async Task<object> ExecuteScenarioAsync(int id)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var steps = new List<string>();

        switch (id)
        {
            case 1:
            {
                // KỊCH BẢN 1: Hai khách giữ cùng ô
                string slotId = "E1";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai} ({slotBefore?.KhoangCachCm}cm). Không có đơn đặt chỗ nào.";
                steps.Add($"Chuẩn bị: Đưa ô {slotId} về FREE.");

                var req1 = new CreateBookingRequest
                {
                    ThoiGianHenT = now.AddMinutes(10),
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-111.11" } }
                };
                var order1 = await _orderService.CreateBookingAsync(2, req1);
                steps.Add($"Khách 1 đặt ô {slotId} thành công (Đơn #{order1.MaDon}).");

                string rejectMsg = "";
                bool isRejected = false;
                try
                {
                    var req2 = new CreateBookingRequest
                    {
                        ThoiGianHenT = now.AddMinutes(10),
                        GoiThoiLuongGio = 1,
                        DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-222.22" } }
                    };
                    await _orderService.CreateBookingAsync(3, req2);
                }
                catch (Exception ex)
                {
                    isRejected = true;
                    rejectMsg = ex.Message;
                    steps.Add($"Khách 2 đặt cùng ô {slotId} bị từ chối: {ex.Message}");
                }

                _db.ChangeTracker.Clear();
                var slotAfter = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                var order1After = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order1.MaDon);

                string afterState = $"Ô {slotId}: {slotAfter?.TrangThai}. Đơn #{order1.MaDon}: {order1After?.TrangThai}. Khách 2: Bị từ chối ({rejectMsg}).";
                string actual = $"Khách 1 tạo đơn #{order1.MaDon} thành công (Đơn: {order1After?.TrangThai}, Ô {slotId}: {slotAfter?.TrangThai}). Khách 2 bị chặn: '{rejectMsg}'.";
                bool passed = isRejected && slotAfter?.TrangThai == "HELD" && order1After?.TrangThai == "PENDING_PAYMENT";

                // Kịch bản tự dọn dẹp dữ liệu của mình:
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_1", "INFO", $"Kịch bản 1 hoàn tất: Chống đặt trùng ô {slotId}.", "DEMO");

                return new
                {
                    scenarioId = 1,
                    name = "Hai khách giữ cùng ô",
                    description = "Kiểm tra tính toàn vẹn Serializable: Khách 1 giữ ô thành công, Khách 2 cố giữ tiếp cùng ô sẽ bị từ chối.",
                    beforeState,
                    afterState,
                    expected = "Khách 1 giữ chỗ thành công (HELD), Khách 2 bị từ chối do trùng khung giờ (409 Conflict).",
                    actual,
                    passed,
                    steps
                };
            }

            case 2:
            {
                // KỊCH BẢN 2: EXPIRED (Giữ chỗ không thanh toán, tua 6 phút -> EXPIRED)
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E2";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now.AddMinutes(10),
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-333.33" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                steps.Add($"Tạo đơn #{order.MaDon} ở trạng thái PENDING_PAYMENT (Hạn thanh toán: 5 phút).");

                // Tua thời gian 6 phút
                _timeProvider.Advance(TimeSpan.FromMinutes(6));
                steps.Add("Tua nhanh đồng hồ hệ thống +6 phút (vượt quá 5 phút hạn thanh toán).");

                await _timeoutWorker.ProcessTimeoutsAsync();
                steps.Add("Background Worker quét các đơn hết hạn.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Ô {slotId}: {slot?.TrangThai}.";
                string actual = $"Đơn #{order.MaDon} đã tự động chuyển sang {reloaded?.TrangThai}, ô {slotId} được giải phóng về {slot?.TrangThai}.";
                bool passed = reloaded?.TrangThai == "EXPIRED" && slot?.TrangThai == "FREE";

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_2", "INFO", $"Kịch bản 2 hoàn tất: Đơn #{order.MaDon} -> EXPIRED, ô {slotId} -> FREE.", "DEMO");

                return new
                {
                    scenarioId = 2,
                    name = "Giữ chỗ không thanh toán (EXPIRED)",
                    description = "Tạo đơn giữ ô nhưng không thanh toán, tua đồng hồ quá 5 phút -> Worker tự chuyển EXPIRED và trả ô về FREE.",
                    beforeState,
                    afterState,
                    expected = "Đơn tự chuyển EXPIRED sau 5 phút không thanh toán, giải phóng ô E2 về FREE.",
                    actual,
                    passed,
                    steps
                };
            }

            case 3:
            {
                // KỊCH BẢN 3: NO_SHOW (Khách đến muộn quá T+15 -> NO_SHOW)
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E3";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now,
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-444.44" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                steps.Add($"Tạo đơn #{order.MaDon} đã thanh toán (PAID). Giờ hẹn T = {now.AddHours(7):HH:mm}, Hạn check-in = T+15 phút.");

                // Tua thời gian 16 phút
                _timeProvider.Advance(TimeSpan.FromMinutes(16));
                steps.Add("Tua nhanh đồng hồ hệ thống +16 phút (quá hạn T+15 phút).");

                await _timeoutWorker.ProcessTimeoutsAsync();
                steps.Add("Background Worker quét các đơn quá hạn check-in.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Ô {slotId}: {slot?.TrangThai}.";
                string actual = $"Đơn #{order.MaDon} đã chuyển sang {reloaded?.TrangThai} (không hoàn tiền), ô {slotId} được giải phóng về {slot?.TrangThai}.";
                bool passed = reloaded?.TrangThai == "NO_SHOW" && slot?.TrangThai == "FREE";

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_3", "INFO", $"Kịch bản 3 hoàn tất: Đơn #{order.MaDon} -> NO_SHOW, ô {slotId} -> FREE.", "DEMO");

                return new
                {
                    scenarioId = 3,
                    name = "Khách đến muộn quá hạn Check-in (NO_SHOW)",
                    description = "Khách đã thanh toán nhưng không đến bãi trong vòng T+15 phút -> Worker chuyển NO_SHOW không hoàn tiền, trả ô về FREE.",
                    beforeState,
                    afterState,
                    expected = "Đơn chuyển NO_SHOW sau T+15 phút không đến bãi, không hoàn tiền, nhả ô E3 về FREE.",
                    actual,
                    passed,
                    steps
                };
            }

            case 4:
            {
                // KỊCH BẢN 4: Quét mã ngoài cổng không vào -> Vô hiệu sau 60s, không kéo dài hạn
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E4";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now,
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-555.55" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                var paidOrder = await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                string entryCode = paidOrder.EntryCodes.First().MaEntryCode;
                steps.Add($"Tạo đơn #{order.MaDon} (PAID), sinh mã EntryCode: {entryCode}.");

                var scanRes = await _gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = entryCode, BienSo = "51H-555.55" });
                steps.Add($"Xe quét mã tại cổng vào thành công: Barie mở trong {scanRes.GateTransitSec} giây.");

                // Không có tín hiệu PASS qua cổng, tua thời gian 5 phút
                _timeProvider.Advance(TimeSpan.FromMinutes(5));
                steps.Add("Xe không đi qua barie. Tua thời gian +5 phút.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Ô {slotId}: {slot?.TrangThai}, Barie đã đóng.";
                string actual = $"Đơn #{order.MaDon} vẫn ở trạng thái {reloaded?.TrangThai}, không bị nhảy sang ENTERED vì chưa có tín hiệu cảm biến PASS.";
                bool passed = reloaded?.TrangThai == "PAID";

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_4", "INFO", $"Kịch bản 4 hoàn tất: Đơn #{order.MaDon} giữ nguyên PAID, barie đã tự đóng.", "DEMO");

                return new
                {
                    scenarioId = 4,
                    name = "Quét mã ngoài cổng nhưng không vào",
                    description = "Quét mã thành công mở barie 60s nhưng xe không đi qua -> Barie đóng, hạn check-in không bị kéo dài vô cớ.",
                    beforeState,
                    afterState,
                    expected = "Cổng mở 60s, xe không đi qua thì cổng đóng, đơn giữ nguyên trạng thái PAID và hạn check-in gốc.",
                    actual,
                    passed,
                    steps
                };
            }

            case 5:
            {
                // KỊCH BẢN 5: Vào bãi rồi quay ra -> ABANDONED
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E5";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now,
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-666.66" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                var paidOrder = await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                string entryCode = paidOrder.EntryCodes.First().MaEntryCode;

                await _gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = entryCode, BienSo = "51H-666.66" });
                await _gateService.ProcessGateInPassAsync(order.MaDon);
                steps.Add($"Xe qua barie cổng vào thành công -> Đơn #{order.MaDon} chuyển sang ENTERED.");

                // Xe quay xe ra thẳng cổng ra
                var outRes = await _gateService.ScanGateOutAsync(new GateOutScanRequest { OrderId = order.MaDon, BienSo = "51H-666.66" });
                steps.Add($"Xe quét ra tại cổng ra mà chưa từng đỗ vào ô: {outRes.Message}");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Ô {slotId}: {slot?.TrangThai}.";
                string actual = $"Đơn #{order.MaDon} đã chuyển {reloaded?.TrangThai}, ô {slotId} được giải phóng về {slot?.TrangThai}.";
                bool passed = reloaded?.TrangThai == "ABANDONED" && slot?.TrangThai == "FREE";

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_5", "INFO", $"Kịch bản 5 hoàn tất: Đơn #{order.MaDon} -> ABANDONED, ô {slotId} -> FREE.", "DEMO");

                return new
                {
                    scenarioId = 5,
                    name = "Vào bãi rồi quay ra ngay (ABANDONED)",
                    description = "Xe đã qua cổng vào (ENTERED) nhưng lập tức quay ra cổng mà không đỗ vào ô -> Chuyển ABANDONED, nhả ô đỗ về FREE.",
                    beforeState,
                    afterState,
                    expected = "Xe vào bãi nhưng quay ra ngay mà chưa đỗ ô -> Chuyển ABANDONED, nhả ô về FREE.",
                    actual,
                    passed,
                    steps
                };
            }

            case 6:
            {
                // KỊCH BẢN 6: Bãi đông, checkpoint gia hạn, nút 'tôi đang kẹt'
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E6";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Thời gian tìm ô mặc định: 5 phút.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now,
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-777.77" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                var paid = await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                await _gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = paid.EntryCodes.First().MaEntryCode, BienSo = "51H-777.77" });
                await _gateService.ProcessGateInPassAsync(order.MaDon);
                steps.Add($"Xe vào cổng thành công (ENTERED), thời gian tìm ô ban đầu: 5 phút.");

                // Qua CP1
                await _gateService.PassCheckpointAsync("CP1", order.MaDon);
                steps.Add("Xe đi qua trạm kiểm soát CP1: Tự động gia hạn thêm +5 phút tìm ô.");

                // Bấm nút kẹt xe
                await _gateService.ReportTrafficJamAsync(order.MaDon, 2);
                steps.Add("Khách bấm nút 'Tôi đang kẹt xe' trên điện thoại: Gia hạn thêm +5 phút nữa.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Ghi chú: '{reloaded?.GhiChu}', Lượt tự gia hạn: {reloaded?.SoLanGiaHan}, Hạn mới: {reloaded?.HanDoVaoO?.AddHours(7):HH:mm:ss}.";
                string actual = $"Đã gia hạn thành công qua trạm CP1 ({reloaded?.GhiChu?.Contains("[CP:CP1]")}) và nút kẹt xe ({reloaded?.SoLanGiaHan} lần), tổng hạn vào ô tăng thêm 10 phút.";
                bool passed = reloaded?.GhiChu?.Contains("[CP:CP1]") == true && reloaded?.SoLanGiaHan >= 1;

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_6", "INFO", $"Kịch bản 6 hoàn tất: Đơn #{order.MaDon} gia hạn thành công (CP1 + Khách kẹt xe).", "DEMO");

                return new
                {
                    scenarioId = 6,
                    name = "Bãi đông, checkpoint gia hạn & Nút 'Tôi đang kẹt'",
                    description = "Xe đang tìm chỗ được tự động gia hạn khi qua trạm Checkpoint CP1 và khi khách bấm 'Tôi đang kẹt' trên app.",
                    beforeState,
                    afterState,
                    expected = "Qua CP1 gia hạn +5 phút, bấm 'Tôi đang kẹt' gia hạn thêm +5 phút (tổng +10 phút, 2 nguồn).",
                    actual,
                    passed,
                    steps
                };
            }

            case 7:
            {
                // KỊCH BẢN 7: Hết hạn đi vào ô -> Tự đổi ô
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E7";
                await FreeSlotDirectAsync(slotId);
                await FreeSlotDirectAsync("E8");
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô ban đầu {slotId}: {slotBefore?.TrangThai}. Ô dự phòng E8: FREE.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now,
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-888.88" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                var paid = await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                await _gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = paid.EntryCodes.First().MaEntryCode, BienSo = "51H-888.88" });
                await _gateService.ProcessGateInPassAsync(order.MaDon);
                steps.Add($"Xe qua cổng hướng về ô {slotId} (Hạn vào ô: 10 phút).");

                // Tua 11 phút (vượt quá thời gian đi vào ô ParkingTimeoutMin = 10 phút)
                _timeProvider.Advance(TimeSpan.FromMinutes(11));
                steps.Add("Tua nhanh đồng hồ +11 phút (quá hạn thời gian vào ô).");

                await _timeoutWorker.ProcessTimeoutsAsync();
                steps.Add("Worker phát hiện quá hạn -> Tự động tìm ô FREE khác và chuyển xe sang ô mới.");

                _db.ChangeTracker.Clear();
                var oldSlot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                var reloaded = await _db.DonDatChos.AsNoTracking().Include(d => d.DonDatChoViTris).FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                string newAssignedSlot = reloaded?.DonDatChoViTris.FirstOrDefault()?.MaViTri ?? "";
                var newSlot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == newAssignedSlot);

                string afterState = $"Ô cũ {slotId}: {oldSlot?.TrangThai}, Đơn #{order.MaDon} được gán sang ô mới: {newAssignedSlot} ({newSlot?.TrangThai}).";
                string actual = $"Đơn #{order.MaDon} đã đổi sang ô mới ({newAssignedSlot}), ô cũ {slotId} chuyển về {oldSlot?.TrangThai}.";
                bool passed = oldSlot?.TrangThai == "FREE" && !string.IsNullOrEmpty(newAssignedSlot) && newAssignedSlot != slotId;

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                if (!string.IsNullOrEmpty(newAssignedSlot)) await FreeSlotDirectAsync(newAssignedSlot);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_7", "INFO", $"Kịch bản 7 hoàn tất: Đơn #{order.MaDon} đổi sang {newAssignedSlot}, ô cũ {slotId} -> FREE.", "DEMO");

                return new
                {
                    scenarioId = 7,
                    name = "Quá hạn vào ô tự động đổi ô mới",
                    description = "Xe qua cổng nhưng không vào ô trong vòng 10 phút -> Worker tự chuyển sang ô FREE khác, nhả ô cũ về FREE.",
                    beforeState,
                    afterState,
                    expected = "Hết hạn 10 phút vào ô -> Hệ thống tự động chuyển sang ô FREE khác, nhả ô cũ về FREE.",
                    actual,
                    passed,
                    steps
                };
            }

            case 8:
            {
                // KỊCH BẢN 8: Đỗ lấn ô -> OVERLAP
                string s1 = "E1";
                string s2 = "E2";
                await FreeSlotDirectAsync(s1);
                await FreeSlotDirectAsync(s2);
                _db.ChangeTracker.Clear();

                var slot1Before = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == s1);
                var slot2Before = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == s2);
                string beforeState = $"Ô {s1}: {slot1Before?.TrangThai} ({slot1Before?.KhoangCachCm}cm), Ô {s2}: {slot2Before?.TrangThai} ({slot2Before?.KhoangCachCm}cm).";

                await _gateService.ProcessSlotTelemetryAsync(s1, 60.0, "DEMO");
                steps.Add($"Cảm biến ô {s1} gửi cự ly 60 cm (khoảng cách lấn vạch 40-80cm).");

                await _gateService.ProcessSlotTelemetryAsync(s2, 65.0, "DEMO");
                steps.Add($"Cảm biến ô liền kề {s2} gửi cự ly 65 cm -> Kích hoạt thuật toán OVERLAP 2 ô kề nhau.");

                _db.ChangeTracker.Clear();
                var slot1 = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == s1);
                var slot2 = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == s2);

                string afterState = $"Ô {s1}: {slot1?.TrangThai} ({slot1?.KhoangCachCm}cm), Ô {s2}: {slot2?.TrangThai} ({slot2?.KhoangCachCm}cm).";
                string actual = $"Hệ thống phát hiện cự ly lấn vạch trên cả 2 ô liền kề: Ô {s1} chuyển sang {slot1?.TrangThai}, ô {s2} chuyển sang {slot2?.TrangThai}. Đã gửi cảnh báo OVERLAP.";
                bool passed = slot1?.TrangThai == "OVERLAP" && slot2?.TrangThai == "OVERLAP";

                // Cleanup:
                await FreeSlotDirectAsync(s1);
                await FreeSlotDirectAsync(s2);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_8", "INFO", $"Kịch bản 8 hoàn tất: Kích hoạt OVERLAP tại {s1} & {s2}.", "DEMO");

                return new
                {
                    scenarioId = 8,
                    name = "Đỗ lấn vạch liền kề (OVERLAP)",
                    description = "Hai ô liền kề cùng đo được cự ly 40-80cm -> Kích hoạt OVERLAP, đổi màu LED sang YELLOW và báo động Staff.",
                    beforeState,
                    afterState,
                    expected = "Hai ô liền kề cùng đo được cự ly 40-80cm -> Kích hoạt OVERLAP, LED vàng, cảnh báo Staff.",
                    actual,
                    passed,
                    steps
                };
            }

            case 9:
            {
                // KỊCH BẢN 9: Mất điện thiết bị, Xe đỗ chui & Phục hồi Audit
                _timeProvider.ResetOffset();
                var eSlots = await _db.ViTriDoXes.Where(s => s.Hang == "E").OrderBy(s => s.Cot).ToListAsync();
                foreach (var s in eSlots)
                {
                    await FreeSlotDirectAsync(s.MaViTri);
                }
                _db.ChangeTracker.Clear();

                string beforeState = "Hàng E (8 ô E1-E8): Tất cả đều FREE (150cm). Không có đơn đặt chỗ nào.";
                steps.Add("Chuẩn bị: Toàn bộ 8 ô hàng E ở trạng thái FREE.");

                // PHA 1: Mất điện thiết bị (MQTT LWT retained = OFFLINE)
                var slotsToOffline = await _db.ViTriDoXes.Where(s => s.Hang == "E").ToListAsync();
                foreach (var s in slotsToOffline)
                {
                    s.TrangThai = "UNKNOWN";
                    s.LanCapNhat = _timeProvider.GetUtcNow().UtcDateTime;
                }
                _db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    LoaiSuKien = "DEVICE_OFFLINE_LWT",
                    NguonSuKien = "MQTT_LWT",
                    MucDo = "ERROR",
                    NoiDung = "Cắt điện thiết bị cảm biến hàng E (MQTT LWT retained=OFFLINE). 8 ô hàng E chuyển sang UNKNOWN."
                });
                await _db.SaveChangesAsync();
                _db.ChangeTracker.Clear();

                var phase1Slots = await _db.ViTriDoXes.AsNoTracking().Where(s => s.Hang == "E").Select(s => s.TrangThai).Distinct().ToListAsync();
                steps.Add("Pha 1 (Mất điện): MQTT broker phát hiện node hàng E mất kết nối (LWT), cập nhật 8 ô hàng E sang UNKNOWN.");

                // PHA 2: Xe đỗ chui vào ô E4 trong lúc mất điện (khoảng cách 30cm, không có đơn hàng hợp lệ)
                var slotE4 = await _db.ViTriDoXes.FindAsync("E4");
                if (slotE4 != null)
                {
                    slotE4.KhoangCachCm = 30m; // Có xe đỗ
                    await _db.SaveChangesAsync();
                }
                _db.ChangeTracker.Clear();
                steps.Add("Pha 2 (Đỗ chui): Trong lúc mất điện, một xe lạ đã đỗ vào ô E4 (khoảng cách 30cm), không hề có đơn đặt chỗ trong CSDL.");

                // PHA 3: Có điện lại (ONLINE) & Hệ thống chạy Audit đối soát
                await TogglePower(new DemoPowerRequest { State = "ONLINE" });
                steps.Add("Pha 3 (Có điện lại & Audit): Node gửi trạng thái ONLINE, Server tự động đối soát phần cứng vs CSDL.");

                _db.ChangeTracker.Clear();
                var afterE4 = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == "E4");
                var latestAuditLog = await _db.NhatKyHeThongs.AsNoTracking()
                    .Where(n => n.LoaiSuKien == "UNAUTHORIZED_PARKING")
                    .OrderByDescending(n => n.ThoiGianGhiNhan)
                    .FirstOrDefaultAsync();

                var eOtherFree = await _db.ViTriDoXes.AsNoTracking()
                    .Where(s => s.Hang == "E" && s.MaViTri != "E4")
                    .AllAsync(s => s.TrangThai == "FREE");

                bool detectedUnauthorized = latestAuditLog?.NoiDung?.Contains("E4") == true;
                bool passed = phase1Slots.Count == 1 && phase1Slots[0] == "UNKNOWN" && detectedUnauthorized && eOtherFree && afterE4?.TrangThai == "OCCUPIED";

                string afterState = $"Pha 1: Hàng E chuyển UNKNOWN. Pha 2: Xe đỗ chui E4 (30cm). Pha 3: E4 chuyển {afterE4?.TrangThai} (Cảnh báo UNAUTHORIZED_PARKING), các ô khác phục hồi về FREE.";
                string actual = $"Đã kiểm thử đủ 3 pha: Mất điện -> 8 ô UNKNOWN; Có điện lại -> Audit phát hiện xe đỗ chui tại ô E4 (Alert: '{latestAuditLog?.LoaiSuKien}'), 7 ô còn lại phục hồi về FREE an toàn.";

                // Cleanup sau kịch bản:
                await FreeSlotDirectAsync("E4");
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_9", "INFO", "Kịch bản 9 hoàn tất: Kiểm thử đủ 3 pha mất điện, đỗ chui và audit.", "DEMO");

                return new
                {
                    scenarioId = 9,
                    name = "Mất điện thiết bị, Xe đỗ chui & Phục hồi Audit",
                    description = "Mất điện: Hàng E chuyển UNKNOWN; Có điện lại: Audit phát hiện xe đỗ chui tại ô E4 (không có đơn) và kích hoạt cảnh báo UNAUTHORIZED_PARKING.",
                    beforeState,
                    afterState,
                    expected = "Pha 1: Hàng E chuyển UNKNOWN; Pha 2: Xe đỗ chui tại E4 (30cm); Pha 3: Audit đối soát phát hiện đỗ chui, kích hoạt UNAUTHORIZED_PARKING, các ô còn lại về FREE.",
                    actual,
                    passed,
                    steps
                };
            }

            case 10:
            {
                // KỊCH BẢN 10: Đỗ quá giờ -> OVERSTAY
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "E8";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now,
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51H-999.00" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                var paid = await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                await _gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = paid.EntryCodes.First().MaEntryCode, BienSo = "51H-999.00" });
                await _gateService.ProcessGateInPassAsync(order.MaDon);
                await _gateService.ProcessSlotTelemetryAsync(slotId, 30.0, "DEMO"); // PARKED
                steps.Add($"Xe đỗ vào ô {slotId} (PARKED), gói thời lượng: 1 giờ.");

                // Tua 75 phút (quá hạn 15 phút)
                _timeProvider.Advance(TimeSpan.FromMinutes(75));
                steps.Add("Tua nhanh đồng hồ +75 phút (quá giờ đã đặt 15 phút).");

                // Quét cổng ra
                var outScan = await _gateService.ScanGateOutAsync(new GateOutScanRequest { OrderId = order.MaDon, BienSo = "51H-999.00" });
                steps.Add($"Xe quét ra tại barie cổng ra: {outScan.Message} (Phí phạt: {outScan.PenaltyFee:N0} VNĐ).");

                // Nhân viên thu phạt
                await _gateService.ConfirmOverstayPaymentAsync(new OverstayConfirmRequest
                {
                    OrderId = order.MaDon,
                    AmountPaid = outScan.PenaltyFee,
                    StaffNote = "Demo thu phạt quá giờ"
                }, "staff01");
                steps.Add("Nhân viên tại quầy thu phí phạt và mở cổng barie ra.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Tiền phạt thu: {reloaded?.TienPhat:N0}đ, Ô {slotId}: {slot?.TrangThai}.";
                string actual = $"Cổng ra chặn xe OVERSTAY quá 15 phút, tính phạt đúng {outScan.PenaltyFee:N0}đ (15 phút × 200đ/phút). Sau khi Staff thu tiền, đơn chuyển sang {reloaded?.TrangThai}, ô {slotId} về {slot?.TrangThai}.";
                bool passed = outScan.RequireStaffPayment && outScan.PenaltyFee == 3000m && reloaded?.TrangThai == "COMPLETED";

                // Cleanup:
                _timeProvider.ResetOffset();
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_10", "INFO", $"Kịch bản 10 hoàn tất: Đơn #{order.MaDon} OVERSTAY -> Thu phạt -> COMPLETED.", "DEMO");

                return new
                {
                    scenarioId = 10,
                    name = "Xe đỗ quá giờ đặt (OVERSTAY) & Thu phạt tại cổng",
                    description = "Xe đỗ quá thời hạn đã mua -> Chặn cổng ra, tính phí phạt theo phút (200đ/phút), Staff thu tiền thì mở cổng.",
                    beforeState,
                    afterState,
                    expected = "Quá giờ 15 phút -> Chặn cổng ra, tính phí phạt 3.000đ (200đ/phút), Staff thu tiền thì mở cổng.",
                    actual,
                    passed,
                    steps
                };
            }

            case 11:
            {
                // KỊCH BẢN 11: Thanh toán chuyển khoản (Bank Transfer)
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "A1";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now.AddMinutes(15),
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51A-111.99" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                steps.Add($"Khách tạo đơn #{order.MaDon} chọn hình thức Chuyển khoản (PENDING_PAYMENT).");

                var paid = await _orderService.SimulateDemoPaymentAsync(order.MaDon, 2, "Customer");
                string entryCode = paid.EntryCodes.FirstOrDefault()?.MaEntryCode ?? "";
                steps.Add($"Thực hiện thanh toán chuyển khoản: Đơn chuyển {paid.TrangThai}, sinh EntryCode: {entryCode}.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().Include(d => d.MaVaoCongs).FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, Mã EntryCode: {entryCode}, Ô {slotId}: {slot?.TrangThai}.";
                string actual = $"Đơn #{order.MaDon} chuyển {reloaded?.TrangThai} thành công sau thanh toán chuyển khoản, sinh mã mở cổng {entryCode}.";
                bool passed = reloaded?.TrangThai == "PAID" && !string.IsNullOrEmpty(entryCode);

                // Cleanup:
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_11", "INFO", $"Kịch bản 11 hoàn tất: Đơn #{order.MaDon} chuyển PAID qua chuyển khoản.", "DEMO");

                return new
                {
                    scenarioId = 11,
                    name = "Thanh toán chuyển khoản ngân hàng",
                    description = "Mô phỏng thanh toán chuyển khoản đúng mã giao dịch và số tiền -> Đơn chuyển PAID, sinh mã EntryCode.",
                    beforeState,
                    afterState,
                    expected = "Thanh toán chuyển khoản khớp mã và số tiền -> Đơn chuyển PAID, sinh EntryCode, idempotent.",
                    actual,
                    passed,
                    steps
                };
            }

            case 12:
            {
                // KỊCH BẢN 12: Thanh toán tiền mặt (CashCode 6 ký tự Crockford)
                _timeProvider.ResetOffset();
                now = _timeProvider.GetUtcNow().UtcDateTime;
                string slotId = "A2";
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                var slotBefore = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);
                string beforeState = $"Ô {slotId}: {slotBefore?.TrangThai}. Không có đơn đặt chỗ nào.";

                var req = new CreateBookingRequest
                {
                    ThoiGianHenT = now.AddMinutes(15),
                    GoiThoiLuongGio = 1,
                    DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = slotId, BienSo = "51A-222.99" } }
                };
                var order = await _orderService.CreateBookingAsync(2, req);
                steps.Add($"Khách tạo đơn #{order.MaDon} chọn hình thức Tiền mặt tại quầy (PENDING_PAYMENT).");

                string cashCode = await _orderService.StaffCreateCashCodeAsync(order.MaDon, 1);
                steps.Add($"Nhân viên tại quầy thu tiền mặt và cấp mã CashCode 6 ký tự Crockford: {cashCode}.");

                var redeemed = await _orderService.RedeemCashCodeAsync(new RedeemCashCodeRequest { MaDon = order.MaDon, MaCashCode = cashCode }, 2, "Customer");
                string entryCode = redeemed.EntryCodes.FirstOrDefault()?.MaEntryCode ?? "";
                steps.Add($"Khách nhập mã CashCode '{cashCode}' trên ứng dụng -> Đơn kích hoạt sang {redeemed.TrangThai}, sinh EntryCode: {entryCode}.");

                _db.ChangeTracker.Clear();
                var reloaded = await _db.DonDatChos.AsNoTracking().Include(d => d.MaVaoCongs).FirstOrDefaultAsync(d => d.MaDon == order.MaDon);
                var cashRec = await _db.MaTienMats.AsNoTracking().FirstOrDefaultAsync(c => c.MaDon == order.MaDon);
                var slot = await _db.ViTriDoXes.AsNoTracking().FirstOrDefaultAsync(s => s.MaViTri == slotId);

                string afterState = $"Đơn #{order.MaDon}: {reloaded?.TrangThai}, CashCode: '{cashCode}' (Đã dùng: {cashRec?.DaSuDung}), Mã EntryCode: {entryCode}, Ô {slotId}: {slot?.TrangThai}.";
                string actual = $"Staff cấp mã CashCode 6 ký tự ({cashCode}). Khách quy đổi thành công -> Đơn chuyển {reloaded?.TrangThai}, sinh mã {entryCode}.";
                bool passed = reloaded?.TrangThai == "PAID" && cashRec?.DaSuDung == true && cashCode.Length == 6;

                // Cleanup:
                await FreeSlotDirectAsync(slotId);
                _db.ChangeTracker.Clear();

                await _notifier.NotifySystemLogAsync("SCENARIO_12", "INFO", $"Kịch bản 12 hoàn tất: Đơn #{order.MaDon} kích hoạt PAID qua CashCode 6 ký tự.", "DEMO");

                return new
                {
                    scenarioId = 12,
                    name = "Thanh toán tiền mặt & Mã CashCode",
                    description = "Nhân viên thu tiền mặt tại quầy phát hành CashCode 6 ký tự Crockford Base32, khách nhập CashCode để kích hoạt vé đỗ xe PAID.",
                    beforeState,
                    afterState,
                    expected = "Staff cấp mã CashCode 6 ký tự Crockford, khách nhập CashCode -> Đơn chuyển PAID, sinh EntryCode.",
                    actual,
                    passed,
                    steps
                };
            }

            default:
                throw new ArgumentException($"Không tìm thấy kịch bản {id}. Chỉ hỗ trợ kịch bản 1 đến 12.");
        }
    }

    private async Task FreeSlotDirectAsync(string slotId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 1. Hủy các đơn active cũ liên quan đến ô này để tránh conflict trùng giờ khi test nhiều lần
        var activeSlotOrders = await _db.DonDatChoViTris
            .Include(v => v.DonDatCho)
            .Where(v => v.MaViTri == slotId &&
                        (v.DonDatCho.TrangThai == "PENDING_PAYMENT" ||
                         v.DonDatCho.TrangThai == "HELD" ||
                         v.DonDatCho.TrangThai == "PAID" ||
                         v.DonDatCho.TrangThai == "ENTERED" ||
                         v.DonDatCho.TrangThai == "PARKED"))
            .ToListAsync();

        foreach (var b in activeSlotOrders)
        {
            b.DonDatCho.TrangThai = "CANCELLED";
        }

        // 2. Dọn các đơn ENTERED tồn đọng từ các lần test trước để không nghẽn cổng vào (MaxCarsFindingSlot)
        var lingeringEntered = await _db.DonDatChos
            .Where(d => d.TrangThai == "ENTERED")
            .ToListAsync();
        foreach (var o in lingeringEntered)
        {
            o.TrangThai = "CANCELLED";
        }

        var slot = await _db.ViTriDoXes.FindAsync(slotId);
        if (slot != null)
        {
            slot.TrangThai = "FREE";
            slot.KhoangCachCm = 150m;
            slot.LanCapNhat = now;
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Lấy danh sách nhật ký hệ thống gần nhất (hỗ trợ lọc theo mức độ và thời gian)
    /// </summary>
    [HttpGet("/api/system/logs")]
    public async Task<IActionResult> GetSystemLogs(
        [FromQuery] string? level,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int limit = 50)
    {
        int take = Math.Clamp(limit, 10, 500);
        var query = _db.NhatKyHeThongs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(level) && level.Trim().ToUpperInvariant() != "ALL")
        {
            string lvl = level.Trim().ToUpperInvariant();
            query = query.Where(x => x.MucDo == lvl);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(x => x.ThoiGianGhiNhan >= fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            DateTime toDateEx = toDate.Value.Date.AddDays(1);
            query = query.Where(x => x.ThoiGianGhiNhan < toDateEx);
        }

        var logs = await query
            .OrderByDescending(x => x.ThoiGianGhiNhan)
            .Take(take)
            .Select(x => new
            {
                id = x.MaNhatKy,
                orderId = x.MaDon,
                eventType = x.LoaiSuKien,
                source = x.NguonSuKien,
                level = x.MucDo,
                content = x.NoiDung,
                topic = x.Mqtttopic,
                time = x.ThoiGianGhiNhan.ToString("HH:mm:ss dd/MM/yyyy")
            })
            .ToListAsync();

        return Ok(logs);
    }

    /// <summary>
    /// Xóa toàn bộ nhật ký hệ thống
    /// </summary>
    [HttpDelete("/api/system/logs")]
    public async Task<IActionResult> ClearSystemLogs()
    {
        CheckDemoMode();

        _db.NhatKyHeThongs.RemoveRange(_db.NhatKyHeThongs);
        await _db.SaveChangesAsync();

        await _notifier.NotifySystemLogAsync("LOGS_CLEARED", "INFO", "Đã xóa toàn bộ nhật ký hệ thống.", "DEMO");
        return Ok(new { success = true, message = "Đã xóa toàn bộ nhật ký." });
    }

    /// <summary>
    /// Điều khiển barie thủ công từ trang Admin hoặc Demo
    /// </summary>
    [HttpPost("/api/gate/barrier/control")]
    public async Task<IActionResult> ControlBarrier([FromBody] BarrierControlRequest request)
    {
        string gate = (request.Gate ?? "IN").Trim().ToUpperInvariant();
        string action = (request.Action ?? "OPEN").Trim().ToUpperInvariant();
        string topic = $"dacn-smartparking/lot1/gate/{gate.ToLowerInvariant()}/cmd";

        string payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            cmd = action,
            source = "MANUAL_UI",
            gate
        });

        await _mqtt.PublishAsync(topic, payload);

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            LoaiSuKien = $"BARRIER_MANUAL_{action}",
            NguonSuKien = "MANUAL_UI",
            MucDo = "INFO",
            NoiDung = $"Điều khiển thủ công barie cổng {gate}: {action}."
        });

        await _db.SaveChangesAsync();

        await _notifier.NotifyBarrierAsync(gate, action);
        await _notifier.NotifySystemLogAsync($"BARRIER_{action}", "INFO", $"Điều khiển thủ công cổng {gate}: {action}", "MANUAL_UI");

        return Ok(new
        {
            success = true,
            gate,
            action,
            message = $"Đã phát lệnh {action} barie cổng {gate}."
        });
    }

    /// <summary>
    /// Lấy danh sách tất cả các đơn đặt chỗ phục vụ Admin
    /// </summary>
    [HttpGet("/api/bookings/admin/all")]
    public async Task<IActionResult> GetAllBookingsForAdmin([FromQuery] string? status = null)
    {
        var query = _db.DonDatChos
            .AsNoTracking()
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .Include(d => d.TaiKhoan)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && status.ToUpperInvariant() != "ALL")
        {
            string s = status.Trim().ToUpperInvariant();
            query = query.Where(d => d.TrangThai == s);
        }

        var list = await query
            .OrderByDescending(d => d.ThoiGianDat)
            .Take(100)
            .Select(d => new
            {
                maDon = d.MaDon,
                khachHang = d.TaiKhoan.HoTen,
                tenDangNhap = d.TaiKhoan.TenDangNhap,
                loaiKhach = d.LoaiKhach,
                thoiGianHenT = d.ThoiGianHenT,
                thoiGianKetThuc = d.ThoiGianKetThucDuKien,
                tongTien = d.TongTien,
                trangThai = d.TrangThai,
                hanThanhToan = d.HanThanhToan,
                hanCheckIn = d.HanCheckIn,
                hanDoVaoO = d.HanDoVaoO,
                viTris = d.DonDatChoViTris.Select(v => v.MaViTri).ToList(),
                bienSos = d.DonDatChoViTris.Select(v => v.BienSo).ToList(),
                entryCode = d.MaVaoCongs.FirstOrDefault(c => c.TrangThai == "ACTIVE" || c.TrangThai == "ENTERED") != null
                    ? d.MaVaoCongs.FirstOrDefault(c => c.TrangThai == "ACTIVE" || c.TrangThai == "ENTERED")!.MaEntryCode
                    : null
            })
            .ToListAsync();

        return Ok(list);
    }
}
