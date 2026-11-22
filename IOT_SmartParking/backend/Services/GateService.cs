using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Dtos;
using SmartParking.Api.Models;

namespace SmartParking.Api.Services;

public class GateService
{
    private readonly SmartParkingDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<ParkingPolicySettings> _policyOptions;
    private readonly IMqttPublisher _mqtt;
    private readonly ILogger<GateService> _logger;
    private readonly IParkingNotificationService? _notifier;

    public GateService(
        SmartParkingDbContext db,
        TimeProvider timeProvider,
        IOptionsMonitor<ParkingPolicySettings> policyOptions,
        IMqttPublisher mqtt,
        ILogger<GateService> logger,
        IParkingNotificationService? notifier = null)
    {
        _db = db;
        _timeProvider = timeProvider;
        _policyOptions = policyOptions;
        _mqtt = mqtt;
        _logger = logger;
        _notifier = notifier;
    }

    private ParkingPolicySettings Policy => _policyOptions.CurrentValue;
    private DateTime CurrentTime => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Hàm nhận diện biển số (Mock ALPR): Lấy trực tiếp từ biển số nhập hoặc trích xuất từ tên file ảnh / base64
    /// </summary>
    public static string ExtractPlate(string? bienSo, string? imageFileName, string? imageBase64)
    {
        if (!string.IsNullOrWhiteSpace(bienSo))
        {
            return bienSo.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(imageFileName))
        {
            string nameOnly = Path.GetFileNameWithoutExtension(imageFileName);
            var match = System.Text.RegularExpressions.Regex.Match(nameOnly, @"[0-9]{2}[A-Z]-[0-9]{4,5}|[0-9]{2}[A-Z][0-9]{4,5}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Value.ToUpperInvariant();
            }
            return nameOnly.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(imageBase64))
        {
            return "51F-88888";
        }

        return string.Empty;
    }

    /// <summary>
    /// Check-in bước 1: Quét EntryCode và nhận diện biển số (Mock ALPR) tại cổng vào.
    /// Kiểm tra tính hợp lệ, giới hạn quét sai (ScanRetryLimit), kiểm tra bãi đông (MaxInLotSearching).
    /// Hợp lệ thì phát lệnh MQTT mở barie cổng vào trong GateTransitSec (60s).
    /// LƯU Ý: Không làm kéo dài hay thay đổi hạn check-in (T + 15 phút) của đơn.
    /// </summary>
    public async Task<GateScanResponseDto> ScanGateInAsync(GateInScanRequest request)
    {
        DateTime now = CurrentTime;
        string cleanCode = (request.EntryCode ?? "").Trim().ToUpperInvariant();
        string cleanPlate = ExtractPlate(request.BienSo, request.ImageFileName, request.ImageBase64);

        if (string.IsNullOrEmpty(cleanPlate))
        {
            throw new ArgumentException("Không thể nhận diện được biển số xe (vui lòng nhập biển số hoặc tải ảnh ALPR hợp lệ).");
        }

        var entry = await _db.MaVaoCongs
            .Include(m => m.DonDatCho)
                .ThenInclude(d => d.DonDatChoViTris)
            .FirstOrDefaultAsync(m => m.MaEntryCode == cleanCode);

        if (entry == null)
        {
            await RecordScanLogAsync(cleanCode, null, "IN", cleanPlate, "DENIED", "Mã vào cổng không tồn tại.");
            throw new ArgumentException("Mã vào cổng (EntryCode) không tồn tại trong hệ thống.");
        }

        var don = entry.DonDatCho;

        // 0. Kiểm tra mã có bị tạm khóa do nhập sai quá số lần không
        if (entry.TrangThai == "LOCKED")
        {
            await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "LOCKED", "Mã vào cổng đang bị tạm khóa.");
            throw new InvalidOperationException("Mã vào cổng (EntryCode) này đang bị tạm khóa do vượt quá số lần quét sai. Vui lòng liên hệ nhân viên để mở khóa.");
        }

        // 1. Kiểm tra trạng thái đơn hàng (phải là PAID)
        if (don.TrangThai != "PAID")
        {
            await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "DENIED", $"Đơn hàng chưa thanh toán hoặc ở trạng thái {don.TrangThai}.");
            throw new InvalidOperationException($"Đơn hàng chưa thanh toán hoặc đang ở trạng thái {don.TrangThai}, không thể mở cổng.");
        }

        // 2. Kiểm tra khung giờ check-in (T đến T + 15 phút)
        DateTime checkInStart = don.ThoiGianHenT;
        DateTime checkInEnd = don.HanCheckIn ?? don.ThoiGianHenT.AddMinutes(Policy.CheckInGraceMin);

        if (now < checkInStart)
        {
            await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "DENIED", "Chưa đến giờ hẹn.");
            throw new InvalidOperationException($"Chưa đến giờ hẹn, quay lại lúc {don.ThoiGianHenT.AddHours(7):HH:mm}.");
        }

        if (now > checkInEnd)
        {
            await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "EXPIRED", "Quá hạn Check-in (T + 15 phút).");
            throw new InvalidOperationException($"Đã quá hạn Check-in ({checkInEnd.AddHours(7):HH:mm}). Đơn đặt chỗ đã bị hủy theo quy định NO_SHOW.");
        }

        // 3. So khớp biển số xe (Mock ALPR) & kiểm tra ScanRetryLimit
        string expectedPlate = (entry.BienSo ?? "").Trim().ToUpperInvariant();
        if (!string.Equals(cleanPlate, expectedPlate, StringComparison.OrdinalIgnoreCase))
        {
            entry.SoLanQuet += 1;

            if (entry.SoLanQuet >= Policy.ScanRetryLimit)
            {
                entry.TrangThai = "LOCKED";
                await _db.SaveChangesAsync();

                await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "LIMIT_EXCEEDED", $"Quét sai biển số {entry.SoLanQuet}/{Policy.ScanRetryLimit} lần. Tạm khóa mã.");
                _db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    MaDon = don.MaDon,
                    LoaiSuKien = "SCAN_LIMIT_EXCEEDED",
                    NguonSuKien = "BACKEND",
                    MucDo = "WARNING",
                    NoiDung = $"Mã {cleanCode} quét sai biển số {entry.SoLanQuet} lần (Biển quét: {cleanPlate}, Biển đúng: {expectedPlate}). Tạm khóa mã."
                });
                await _db.SaveChangesAsync();

                if (_notifier != null)
                {
                    await _notifier.NotifyAlertAsync("CODE_LOCKED", $"Mã vào cổng {cleanCode} đã bị khóa do quét sai biển số {entry.SoLanQuet} lần.", new { entryCode = cleanCode, orderId = don.MaDon });
                }

                throw new InvalidOperationException($"Biển số không khớp ({cleanPlate} != {expectedPlate}). Đã quét sai {entry.SoLanQuet}/{Policy.ScanRetryLimit} lần. Mã vào cổng đã bị tạm khóa.");
            }

            await _db.SaveChangesAsync();
            await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "DENIED", $"Biển số không khớp ({cleanPlate} != {expectedPlate}).");
            throw new ArgumentException($"Biển số nhận diện ({cleanPlate}) không khớp với biển số đăng ký ({expectedPlate}). Bạn còn {Policy.ScanRetryLimit - entry.SoLanQuet} lần thử lại.");
        }

        // 4. Kiểm tra bãi đông xe đang tìm chỗ (MaxInLotSearching)
        int searchingCount = await _db.DonDatChos.CountAsync(d => d.TrangThai == "ENTERED");
        if (searchingCount >= Policy.MaxInLotSearching)
        {
            // Publish lệnh HOLD tới cổng vào
            string holdTopic = "dacn-smartparking/lot1/gate/in/cmd";
            string holdPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                cmd = "HOLD",
                reason = "CONGESTION",
                searchingCount = searchingCount,
                maxAllowed = Policy.MaxInLotSearching,
                message = "Bãi đang đông xe tìm chỗ, barie tạm giữ đóng."
            });
            await _mqtt.PublishAsync(holdTopic, holdPayload);

            if (_notifier != null)
            {
                await _notifier.NotifyBarrierAsync("IN", "HOLD");
                await _notifier.NotifyAlertAsync("GATE_HOLD", $"Cổng vào tạm giữ (HOLD) do bãi đông: {searchingCount}/{Policy.MaxInLotSearching} xe đang tìm chỗ.", new { searchingCount, max = Policy.MaxInLotSearching });
            }

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                MaDon = don.MaDon,
                LoaiSuKien = "GATE_IN_HOLD_CONGESTION",
                NguonSuKien = "BACKEND",
                MucDo = "WARNING",
                NoiDung = $"Bãi xe đang có {searchingCount} xe tìm chỗ (vượt quá MaxInLotSearching = {Policy.MaxInLotSearching}). Cổng vào tạm giữ xe biển số {cleanPlate}."
            });
            await _db.SaveChangesAsync();

            throw new InvalidOperationException($"Bãi xe hiện đang đông xe đang tìm ô đỗ ({searchingCount}/{Policy.MaxInLotSearching} xe). Vui lòng dừng chờ trước barie trong giây lát.");
        }

        // 5. Quét hợp lệ: Ghi nhật ký quét thành công và phát lệnh MQTT mở cổng
        string slotId = don.DonDatChoViTris.FirstOrDefault()?.MaViTri ?? "E1";
        await RecordScanLogAsync(cleanCode, don.MaDon, "IN", cleanPlate, "SUCCESS", $"Quét hợp lệ. Ô đỗ chỉ định: {slotId}");

        string openTopic = "dacn-smartparking/lot1/gate/in/cmd";
        string openPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            cmd = "OPEN",
            barcode = cleanCode,
            plate = cleanPlate,
            spot = slotId,
            orderId = don.MaDon,
            transitSec = Policy.GateTransitSec
        });
        await _mqtt.PublishAsync(openTopic, openPayload);

        if (_notifier != null)
        {
            await _notifier.NotifyBarrierAsync("IN", "OPEN");
            await _notifier.NotifySystemLogAsync("GATE_IN_OPENED", "INFO", $"Mở barie cổng vào cho xe {cleanPlate} vào ô {slotId}.", "GATE");
        }

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "GATE_IN_OPENED",
            NguonSuKien = "BACKEND",
            MucDo = "INFO",
            NoiDung = $"Đã xác thực xe {cleanPlate} qua cổng vào. Barie mở (thời hạn qua cổng: {Policy.GateTransitSec}s). Ô đỗ: {slotId}."
        });
        await _db.SaveChangesAsync();

        return new GateScanResponseDto
        {
            Success = true,
            Message = $"Xác thực thành công. Barie đang mở, vui lòng lái xe qua cổng trong vòng {Policy.GateTransitSec} giây.",
            OrderId = don.MaDon,
            SlotId = slotId,
            GateTransitSec = Policy.GateTransitSec,
            Status = don.TrangThai,
            RecognizedPlate = cleanPlate
        };
    }

    /// <summary>
    /// Check-in bước 2: Xe đã đi qua cổng vào (thiết bị gửi sự kiện PASS).
    /// Cảm biến barie không giữ mã đơn; nếu orderId để trống, hệ thống tự động ghép với đơn hàng vừa được quét mở cổng thành công.
    /// Đơn chuyển sang ENTERED, bắt đầu tính đồng hồ ParkingTimeoutMin (thời gian đi vào ô) từ thời điểm PASS.
    /// Ô đỗ chuyển sang trạng thái GUIDING, phát lệnh dẫn đường guide/cmd theo bảng DuongDanDen.
    /// </summary>
    public async Task<BookingResponseDto> ProcessGateInPassAsync(long? orderId = null)
    {
        DateTime now = CurrentTime;

        if (!orderId.HasValue || orderId.Value <= 0)
        {
            var recentScan = await _db.NhatKyQuetMas
                .Where(n => n.LoaiCong == "IN" && n.KetQua == "SUCCESS" && n.MaDon != null)
                .OrderByDescending(n => n.ThoiGianQuet)
                .FirstOrDefaultAsync();

            if (recentScan != null && recentScan.MaDon.HasValue)
            {
                var candidate = await _db.DonDatChos.FindAsync(recentScan.MaDon.Value);
                if (candidate != null && candidate.TrangThai == "PAID")
                {
                    orderId = candidate.MaDon;
                }
            }

            if (!orderId.HasValue || orderId.Value <= 0)
            {
                var latestPaid = await _db.DonDatChos
                    .Where(d => d.TrangThai == "PAID")
                    .OrderByDescending(d => d.ThoiGianDat)
                    .FirstOrDefaultAsync();

                if (latestPaid != null)
                {
                    orderId = latestPaid.MaDon;
                }
            }
        }

        if (!orderId.HasValue || orderId.Value <= 0)
        {
            throw new KeyNotFoundException("Không tìm thấy đơn hàng đang chờ qua cổng vào (PAID).");
        }

        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .FirstOrDefaultAsync(d => d.MaDon == orderId.Value);

        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId.Value}.");
        }

        // Chuyển đơn sang ENTERED
        don.TrangThai = "ENTERED";
        don.ThoiGianVaoCong = now;

        // Bắt đầu tính đồng hồ đỗ xe ParkingTimeoutMin từ thời điểm xe PASS qua cổng
        don.HanDoVaoO = now.AddMinutes(Policy.ParkingTimeoutMin);

        // Đổi trạng thái mã vào cổng sang ENTERED
        foreach (var code in don.MaVaoCongs)
        {
            if (code.TrangThai == "ACTIVE")
            {
                code.TrangThai = "ENTERED";
                code.ThoiGianSuDung = now;
            }
        }

        // Cập nhật các ô đỗ liên quan sang GUIDING (bật đèn dẫn đường)
        var slotIds = don.DonDatChoViTris.Select(x => x.MaViTri).ToList();
        var slots = await _db.ViTriDoXes.Where(s => slotIds.Contains(s.MaViTri)).ToListAsync();
        foreach (var s in slots)
        {
            s.TrangThai = "GUIDING";
            s.LanCapNhat = now;
        }

        foreach (var detail in don.DonDatChoViTris)
        {
            detail.TrangThai = "GUIDING";
        }

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "GATE_IN_PASSED",
            NguonSuKien = "ESP32",
            MucDo = "INFO",
            NoiDung = $"Xe đã qua barie cổng vào (ENTERED). Hạn vào ô: {don.HanDoVaoO.Value.AddHours(7):HH:mm:ss} ({Policy.ParkingTimeoutMin} phút)."
        });

        await _db.SaveChangesAsync();

        string prefix = Policy.MqttTopicPrefix ?? "dacn-smartparking/lot1";
        string targetSlotId = slotIds.FirstOrDefault() ?? "E1";
        string cleanPlate = don.DonDatChoViTris.FirstOrDefault()?.BienSo ?? "";

        // 1. Publish lệnh đổi đèn LED ô đỗ slot/{id}/cmd sang GREEN (bỏ slot/{id}/led)
        foreach (var slotId in slotIds)
        {
            string slotCmdTopic = $"{prefix}/slot/{slotId}/cmd";
            await _mqtt.PublishAsync(slotCmdTopic, "GREEN", retain: false);
        }

        // 2. Publish dải LED NeoPixel dẫn đường guide/cmd từ bảng DuongDanDen
        var guide = await _db.DuongDanDens.FirstOrDefaultAsync(g => g.MaViTri == targetSlotId);
        if (guide != null)
        {
            var pixels = guide.DanhSachLed.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out int px) ? px : -1)
                .Where(px => px >= 0)
                .ToList();

            string guideTopic = $"{prefix}/guide/cmd";
            string guidePayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                slot = targetSlotId,
                path = pixels,
                color = guide.MauLedDich ?? "GREEN",
                ttl = 120
            });
            await _mqtt.PublishAsync(guideTopic, guidePayload, retain: false);
        }

        // 3. Publish hiển thị màn hình LCD display/cmd
        string displayTopic = $"{prefix}/display/cmd";
        string displayPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            line1 = $"XIN CHAO {cleanPlate}",
            line2 = $"VAO O DO: {targetSlotId}"
        });
        await _mqtt.PublishAsync(displayTopic, displayPayload, retain: false);

        if (_notifier != null)
        {
            await _notifier.NotifyOrderStatusChangedAsync(don.MaDon, "ENTERED", $"Xe đã qua cổng vào, đang di chuyển vào ô {targetSlotId}");
            await _notifier.NotifySlotsReloadAsync();
        }

        return ParkingOrderService.MapToDto(don);
    }

    /// <summary>
    /// Kiểm soát xe ra cổng:
    /// - Nếu xe đang ở trạng thái ENTERED (vào bãi nhưng chưa từng đỗ vào ô mà đã quay ra):
    ///   -> Đơn chuyển sang ABANDONED, giải phóng ô đỗ về FREE hoặc BOOKED_FUTURE, mở cổng ra.
    /// - Nếu xe đã đỗ (PARKED/COMPLETED):
    ///   -> Chuyển COMPLETED, giải phóng ô đỗ, mở cổng ra.
    /// </summary>
    public async Task<GateScanResponseDto> ScanGateOutAsync(GateOutScanRequest request)
    {
        DateTime now = CurrentTime;

        DonDatCho? don = null;

        if (request.OrderId.HasValue && request.OrderId.Value > 0)
        {
            don = await _db.DonDatChos
                .Include(d => d.DonDatChoViTris)
                .Include(d => d.ThanhToans)
                .FirstOrDefaultAsync(d => d.MaDon == request.OrderId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(request.BienSo))
        {
            string cleanPlate = request.BienSo.Trim().ToUpperInvariant();
            don = await _db.DonDatChos
                .Include(d => d.DonDatChoViTris)
                .Include(d => d.ThanhToans)
                .Where(d => d.TrangThai == "ENTERED" || d.TrangThai == "PARKED" || d.TrangThai == "PAID")
                .Where(d => d.DonDatChoViTris.Any(v => v.BienSo == cleanPlate))
                .OrderByDescending(d => d.ThoiGianDat)
                .FirstOrDefaultAsync();
        }

        if (don == null)
        {
            throw new KeyNotFoundException("Không tìm thấy đơn hàng đang hoạt động khớp với thông tin xe ra cổng.");
        }

        var slotIds = don.DonDatChoViTris.Select(x => x.MaViTri).ToList();

        // 1. TÌNH HUỐNG XE RA KHI CHƯA ĐỖ VÀO Ô (ABANDONED)
        if (don.TrangThai == "ENTERED")
        {
            don.TrangThai = "ABANDONED";
            don.ThoiGianRaCong = now;

            // Nhả ô đỗ
            await ParkingTimeoutWorker.ReevaluateSlotsStatusBatchAsync(_db, slotIds, now, Policy.CheckInGraceMin);

            // Mở cổng ra
            string exitTopic = "dacn-smartparking/lot1/gate/out/cmd";
            string exitPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                cmd = "OPEN",
                orderId = don.MaDon,
                reason = "ABANDONED",
                message = "Xe ra khi chưa đỗ vào ô. Cổng ra mở."
            });
            await _mqtt.PublishAsync(exitTopic, exitPayload);

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                MaDon = don.MaDon,
                LoaiSuKien = "VEHICLE_ABANDONED",
                NguonSuKien = "BACKEND",
                MucDo = "WARNING",
                NoiDung = $"Xe ra cổng khi chưa đỗ vào ô ({string.Join(", ", slotIds)}). Đơn chuyển sang ABANDONED."
            });

            await _db.SaveChangesAsync();

            // Kiểm tra bãi đông để tự động mở lại cổng vào nếu số xe tìm chỗ giảm xuống dưới ngưỡng
            await CheckAndAutoReopenGateAsync();

            if (_notifier != null)
            {
                await _notifier.NotifyOrderStatusChangedAsync(don.MaDon, "ABANDONED", "Xe ra cổng khi chưa vào ô");
                await _notifier.NotifySlotsReloadAsync();
            }

            return new GateScanResponseDto
            {
                Success = true,
                Message = "Xe ra bãi khi chưa đỗ vào ô. Cổng ra mở. Đơn hàng chuyển sang trạng thái ABANDONED.",
                OrderId = don.MaDon,
                Status = "ABANDONED"
            };
        }

        // 2. KIỂM TRA QUÁ GIỜ ĐÃ ĐẶT (OVERSTAY)
        if (don.TrangThai == "PARKED")
        {
            DateTime bookedEnd = don.ThoiGianKetThucDuKien;
            DateTime graceEnd = bookedEnd.AddMinutes(Policy.OverstayGraceMin);
            if (now > graceEnd)
            {
                int overstayMin = (int)Math.Max(0, (now - bookedEnd).TotalMinutes);
                decimal penalty = overstayMin * Policy.OverstayPenaltyPerMinute;
                don.TienPhat = penalty;
                await _db.SaveChangesAsync();

                if (_notifier != null)
                {
                    await _notifier.NotifyAlertAsync("OVERSTAY_EXIT_BLOCKED", $"Xe đơn {don.MaDon} quá giờ {overstayMin} phút. Cần thu phí phạt {penalty:N0} VNĐ tại cổng ra.", new { orderId = don.MaDon, overstayMin, penalty });
                }

                return new GateScanResponseDto
                {
                    Success = false,
                    Message = $"Xe đã quá giờ gửi {overstayMin} phút (Phí phạt: {penalty:N0} VNĐ). Vui lòng thanh toán cho nhân viên để mở cổng.",
                    OrderId = don.MaDon,
                    Status = "OVERSTAY",
                    RequireStaffPayment = true,
                    PenaltyFee = penalty,
                    PenaltyMinutes = overstayMin
                };
            }
        }

        // 3. TÌNH HUỐNG XE HOÀN TẤT ĐỖ VÀ RA BÃI BÌNH THƯỜNG
        don.TrangThai = "COMPLETED";
        don.ThoiGianRaCong = now;

        foreach (var detail in don.DonDatChoViTris)
        {
            detail.TrangThai = "COMPLETED";
            detail.ThoiGianRaO ??= now;
        }

        // Đánh giá lại trạng thái ô đỗ
        await ParkingTimeoutWorker.ReevaluateSlotsStatusBatchAsync(_db, slotIds, now, Policy.CheckInGraceMin);

        string normalExitTopic = "dacn-smartparking/lot1/gate/out/cmd";
        string normalExitPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            cmd = "OPEN",
            orderId = don.MaDon,
            status = "COMPLETED"
        });
        await _mqtt.PublishAsync(normalExitTopic, normalExitPayload);

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "VEHICLE_EXIT_COMPLETED",
            NguonSuKien = "BACKEND",
            MucDo = "INFO",
            NoiDung = $"Xe hoàn tất lượt đỗ và đã ra cổng. Đơn hàng {don.MaDon} chuyển COMPLETED."
        });

        await _db.SaveChangesAsync();

        if (_notifier != null)
        {
            await _notifier.NotifyOrderStatusChangedAsync(don.MaDon, "COMPLETED", "Xe đã hoàn tất gửi và ra khỏi bãi");
            await _notifier.NotifySlotsReloadAsync();
        }

        return new GateScanResponseDto
        {
            Success = true,
            Message = "Xe hoàn tất gửi xe. Cổng ra mở.",
            OrderId = don.MaDon,
            Status = "COMPLETED"
        };
    }

    /// <summary>
    /// Nhân viên xác nhận thu phí phạt quá giờ tại cổng ra và mở barie cho xe ra
    /// </summary>
    public async Task<GateScanResponseDto> ConfirmOverstayPaymentAsync(OverstayConfirmRequest request, string? staffName)
    {
        DateTime now = CurrentTime;
        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.ThanhToans)
            .FirstOrDefaultAsync(d => d.MaDon == request.OrderId);

        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {request.OrderId}.");
        }

        decimal amount = request.AmountPaid ?? don.TienPhat;
        _db.ThanhToans.Add(new ThanhToan
        {
            MaDon = don.MaDon,
            SoTien = amount,
            HinhThuc = "TIEN_MAT",
            TrangThai = "PAID",
            ThoiGianThanhToan = now,
            MaGiaoDichDuyNhat = "PENALTY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            GhiChu = request.StaffNote ?? $"Nhân viên {staffName ?? "Staff"} thu phí phạt quá giờ tại cổng ra."
        });

        don.TrangThai = "COMPLETED";
        don.ThoiGianRaCong = now;

        foreach (var detail in don.DonDatChoViTris)
        {
            detail.TrangThai = "COMPLETED";
            detail.ThoiGianRaO ??= now;
        }

        var slotIds = don.DonDatChoViTris.Select(x => x.MaViTri).ToList();
        await ParkingTimeoutWorker.ReevaluateSlotsStatusBatchAsync(_db, slotIds, now, Policy.CheckInGraceMin);

        string exitTopic = "dacn-smartparking/lot1/gate/out/cmd";
        string exitPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            cmd = "OPEN",
            orderId = don.MaDon,
            status = "COMPLETED",
            penaltyPaid = amount
        });
        await _mqtt.PublishAsync(exitTopic, exitPayload);

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "OVERSTAY_RESOLVED",
            NguonSuKien = "STAFF",
            MucDo = "INFO",
            NoiDung = $"Nhân viên {staffName ?? "Staff"} đã thu phí phạt quá giờ {amount:N0} VNĐ và mở cổng ra cho đơn {don.MaDon}."
        });

        await _db.SaveChangesAsync();

        if (_notifier != null)
        {
            await _notifier.NotifyOrderStatusChangedAsync(don.MaDon, "COMPLETED", "Đã thu tiền phạt quá giờ và mở cổng ra");
            await _notifier.NotifySlotsReloadAsync();
        }

        return new GateScanResponseDto
        {
            Success = true,
            Message = $"Đã thu phí phạt {amount:N0} VNĐ thành công. Cổng ra đã mở.",
            OrderId = don.MaDon,
            Status = "COMPLETED"
        };
    }

    /// <summary>
    /// Lấy danh sách mã vào cổng đang bị khóa (LOCKED)
    /// </summary>
    public async Task<List<object>> GetLockedCodesAsync()
    {
        var list = await _db.MaVaoCongs
            .AsNoTracking()
            .Where(m => m.TrangThai == "LOCKED")
            .Include(m => m.DonDatCho)
            .OrderByDescending(m => m.NgayTao)
            .Select(m => new
            {
                code = m.MaEntryCode,
                orderId = m.MaDon,
                plate = m.BienSo,
                soLanQuet = m.SoLanQuet,
                status = m.TrangThai,
                hanSuDung = m.HanSuDung
            })
            .ToListAsync();
        return list.Cast<object>().ToList();
    }

    /// <summary>
    /// Nhân viên mở khóa mã vào cổng (EntryCode) bị khóa do quét sai quá số lần
    /// </summary>
    public async Task<bool> UnlockEntryCodeAsync(string entryCode, string? staffUsername)
    {
        string cleanCode = (entryCode ?? "").Trim().ToUpperInvariant();
        var entry = await _db.MaVaoCongs
            .Include(m => m.DonDatCho)
            .FirstOrDefaultAsync(m => m.MaEntryCode == cleanCode);

        if (entry == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy mã vào cổng {cleanCode}.");
        }

        entry.TrangThai = "ACTIVE";
        entry.SoLanQuet = 0;

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = entry.MaDon,
            LoaiSuKien = "ENTRY_CODE_UNLOCKED",
            NguonSuKien = "STAFF",
            MucDo = "INFO",
            NoiDung = $"Nhân viên {staffUsername ?? "Staff"} đã mở khóa mã vào cổng {cleanCode} và đặt lại số lần quét sai về 0."
        });

        await _db.SaveChangesAsync();

        if (_notifier != null)
        {
            await _notifier.NotifySystemLogAsync("ENTRY_CODE_UNLOCKED", "INFO", $"Nhân viên đã mở khóa mã {cleanCode}.", "STAFF");
        }

        return true;
    }

    /// <summary>
    /// Lấy dữ liệu lộ trình đèn LED dẫn đường từ bảng DuongDanDen và phát lệnh guide/cmd qua MQTT
    /// </summary>
    public async Task<object?> GetGuidePathAsync(string slotId)
    {
        string cleanSlot = (slotId ?? "").Trim().ToUpperInvariant();
        var guide = await _db.DuongDanDens.FirstOrDefaultAsync(g => g.MaViTri == cleanSlot);
        if (guide == null) return null;

        var pixels = new List<int>();
        try
        {
            if (guide.DanhSachLed.StartsWith("["))
            {
                pixels = System.Text.Json.JsonSerializer.Deserialize<List<int>>(guide.DanhSachLed) ?? new List<int>();
            }
            else
            {
                pixels = guide.DanhSachLed.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out int px) ? px : -1)
                    .Where(px => px >= 0)
                    .ToList();
            }
        }
        catch
        {
            pixels = new List<int> { 0, 1, 2, guide.PixelDich };
        }

        string prefix = Policy.MqttTopicPrefix.TrimEnd('/');
        string guideTopic = $"{prefix}/guide/cmd";
        string guidePayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            slot = cleanSlot,
            path = pixels,
            color = guide.MauLedDich ?? "GREEN",
            ttl = 120
        });
        await _mqtt.PublishAsync(guideTopic, guidePayload, retain: false);

        if (_notifier != null)
        {
            await _notifier.NotifySystemLogAsync("GUIDE_CMD", "INFO", $"Kích hoạt dải đèn dẫn đường tới ô {cleanSlot} ({pixels.Count} pixels).", "GUIDE");
        }

        return new
        {
            slotId = cleanSlot,
            path = pixels,
            pixelDich = guide.PixelDich,
            mauLedDich = guide.MauLedDich ?? "#00E5FF",
            ttl = 120
        };
    }

    /// <summary>
    /// Kiểm tra và tự động mở lại cổng vào (UNHOLD) khi số xe đang tìm ô giảm xuống dưới ngưỡng
    /// </summary>
    public async Task CheckAndAutoReopenGateAsync()
    {
        int searchingCount = await _db.DonDatChos.CountAsync(d => d.TrangThai == "ENTERED");
        if (searchingCount < Policy.MaxInLotSearching)
        {
            string topic = "dacn-smartparking/lot1/gate/in/cmd";
            string payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                cmd = "OPEN",
                reason = "UNHOLD",
                searchingCount = searchingCount,
                maxAllowed = Policy.MaxInLotSearching,
                message = "Bãi xe đã thông thoáng, cổng vào tiếp tục hoạt động bình thường."
            });
            await _mqtt.PublishAsync(topic, payload);

            if (_notifier != null)
            {
                await _notifier.NotifyBarrierAsync("IN", "OPEN");
                await _notifier.NotifySystemLogAsync("GATE_UNHOLD", "INFO", $"Bãi xe đã thông thoáng ({searchingCount}/{Policy.MaxInLotSearching} xe). Cổng vào tiếp tục mở.", "BACKEND");
            }
        }
    }

    /// <summary>
    /// Nút "Tôi đang kẹt" trên app khách: Khách hàng yêu cầu gia hạn thêm ExtensionMin (5 phút) để vào ô.
    /// Khống chế tối đa MaxExtensions (1 lần).
    /// </summary>
    public async Task<BookingResponseDto> ReportTrafficJamAsync(long orderId, int customerId)
    {
        DateTime now = CurrentTime;

        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .FirstOrDefaultAsync(d => d.MaDon == orderId);

        if (don == null || don.MaTaiKhoan != customerId)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        if (don.TrangThai != "ENTERED")
        {
            throw new InvalidOperationException($"Chỉ có thể bấm báo kẹt xe khi đơn đang ở trạng thái ENTERED (đang tìm ô). Trạng thái hiện tại: {don.TrangThai}.");
        }

        if (don.SoLanGiaHan >= Policy.MaxExtensions)
        {
            throw new InvalidOperationException($"Bạn đã dùng hết số lượt gia hạn thời gian tìm ô cho phép (tối đa {Policy.MaxExtensions} lần).");
        }

        don.SoLanGiaHan += 1;
        don.HanDoVaoO = (don.HanDoVaoO ?? now).AddMinutes(Policy.ExtensionMin);

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "USER_REPORTED_JAM",
            NguonSuKien = "CUSTOMER_APP",
            MucDo = "INFO",
            NoiDung = $"Khách hàng bấm 'Tôi đang kẹt'. Đã gia hạn thêm {Policy.ExtensionMin} phút. Hạn vào ô mới: {don.HanDoVaoO.Value.AddHours(7):HH:mm:ss}."
        });

        await _db.SaveChangesAsync();

        return ParkingOrderService.MapToDto(don);
    }

    /// <summary>
    /// Xe đi qua cảm biến Checkpoint trên đường nội bộ bãi xe:
    /// Tự động gia hạn thêm ExtensionMin.
    /// Giới hạn: Mỗi checkpoint chỉ gia hạn tối đa 1 lần cho mỗi đơn, và tổng thời gian gia hạn không vượt quá MaxTotalExtensionMin.
    /// </summary>
    public async Task<bool> PassCheckpointAsync(string checkpointId, long? orderId)
    {
        DateTime now = CurrentTime;
        string cleanCp = (checkpointId ?? "").Trim().ToUpperInvariant();

        if (orderId.HasValue && orderId.Value > 0)
        {
            var don = await _db.DonDatChos.FindAsync(orderId.Value);
            if (don != null && don.TrangThai == "ENTERED")
            {
                string note = don.GhiChu ?? "";
                string cpTag = $"[CP:{cleanCp}]";
                if (note.Contains(cpTag))
                {
                    // Checkpoint này đã gia hạn cho đơn này rồi, không gia hạn thêm
                    _logger.LogInformation("Đơn {OrderId} đã quét qua {CpId} trước đó. Bỏ qua gia hạn trùng.", don.MaDon, cleanCp);
                    return false;
                }

                DateTime baseDeadline = don.ThoiGianVaoCong?.AddMinutes(Policy.ParkingTimeoutMin) ?? now.AddMinutes(Policy.ParkingTimeoutMin);
                DateTime currentDeadline = don.HanDoVaoO ?? baseDeadline;
                double currentExtMinutes = Math.Max(0, (currentDeadline - baseDeadline).TotalMinutes);

                if (currentExtMinutes + Policy.ExtensionMin > Policy.MaxTotalExtensionMin)
                {
                    _logger.LogInformation("Đơn {OrderId} đạt trần gia hạn tối đa ({MaxExt}m). Không gia hạn thêm.", don.MaDon, Policy.MaxTotalExtensionMin);
                    return false;
                }

                don.HanDoVaoO = currentDeadline.AddMinutes(Policy.ExtensionMin);
                don.GhiChu = (note + " " + cpTag).Trim();

                _db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    MaDon = don.MaDon,
                    LoaiSuKien = "CHECKPOINT_EXTENSION",
                    NguonSuKien = "ESP32",
                    MucDo = "INFO",
                    NoiDung = $"Xe qua trạm kiểm soát {cleanCp}. Tự động gia hạn thêm {Policy.ExtensionMin} phút tìm ô (Hạn mới: {don.HanDoVaoO.Value.AddHours(7):HH:mm:ss})."
                });

                await _db.SaveChangesAsync();

                if (_notifier != null)
                {
                    await _notifier.NotifySystemLogAsync("CHECKPOINT_PASS", "INFO", $"Đơn {don.MaDon} qua {cleanCp}: gia hạn +{Policy.ExtensionMin}m.", "CHECKPOINT");
                }

                return true;
            }
            return false;
        }
        else
        {
            // Nếu không có orderId cụ thể, duyệt các đơn ENTERED chưa từng qua checkpoint này
            var activeEntered = await _db.DonDatChos.Where(d => d.TrangThai == "ENTERED").ToListAsync();
            bool anyUpdated = false;
            string cpTag = $"[CP:{cleanCp}]";

            foreach (var d in activeEntered)
            {
                string note = d.GhiChu ?? "";
                if (!note.Contains(cpTag))
                {
                    DateTime baseDeadline = d.ThoiGianVaoCong?.AddMinutes(Policy.ParkingTimeoutMin) ?? now.AddMinutes(Policy.ParkingTimeoutMin);
                    DateTime currentDeadline = d.HanDoVaoO ?? baseDeadline;
                    double currentExtMinutes = Math.Max(0, (currentDeadline - baseDeadline).TotalMinutes);

                    if (currentExtMinutes + Policy.ExtensionMin <= Policy.MaxTotalExtensionMin)
                    {
                        d.HanDoVaoO = currentDeadline.AddMinutes(Policy.ExtensionMin);
                        d.GhiChu = (note + " " + cpTag).Trim();
                        anyUpdated = true;
                    }
                }
            }

            if (anyUpdated)
            {
                await _db.SaveChangesAsync();
            }
            return anyUpdated;
        }
    }

    /// <summary>
    /// Hàm xử lý trạng thái ô đỗ thống nhất cho cả cảm biến vật lý (MQTT) và ô ảo (A-D trong DemoMode):
    /// Khi cự ly <= 40cm -> OCCUPIED. Nếu ô đang GUIDING hoặc có đơn ENTERED -> chuyển PARKED, cập nhật giờ vào ô,
    /// tự động kiểm tra mở lại cổng HOLD nếu số xe tìm chỗ giảm.
    /// </summary>
    public async Task<ViTriDoXe> ProcessSlotTelemetryAsync(string slotId, double distanceCm, string source = "SENSOR")
    {
        DateTime now = CurrentTime;
        string cleanId = slotId.Trim().ToUpperInvariant();
        var slot = await _db.ViTriDoXes.FindAsync(cleanId);
        if (slot == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy ô đỗ {cleanId}.");
        }

        string oldStatus = slot.TrangThai;
        string newStatus = oldStatus;

        if (distanceCm <= Policy.OccupiedMaxCm)
        {
            newStatus = "OCCUPIED";
        }
        else if (distanceCm <= Policy.PartialMaxCm)
        {
            newStatus = "OVERLAP";
        }
        else
        {
            if (oldStatus == "GUIDING" || oldStatus == "BOOKED" || oldStatus == "HELD")
            {
                newStatus = oldStatus;
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
            _db.LichSuCamBiens.Add(new LichSuCamBien
            {
                MaViTri = slot.MaViTri,
                KhoangCachCm = slot.KhoangCachCm,
                TrangThaiCu = oldStatus,
                TrangThaiMoi = newStatus,
                ThoiGianGhiNhan = now
            });
        }

        // Nếu ô chuyển OCCUPIED: Kiểm tra xe vào ô khi đang GUIDING / ENTERED
        if (newStatus == "OCCUPIED")
        {
            var activeOrder = await _db.DonDatChos
                .Include(d => d.DonDatChoViTris)
                .Where(d => (d.TrangThai == "ENTERED" || d.TrangThai == "PAID") && d.DonDatChoViTris.Any(v => v.MaViTri == cleanId))
                .FirstOrDefaultAsync();

            if (activeOrder != null)
            {
                activeOrder.TrangThai = "PARKED";
                foreach (var detail in activeOrder.DonDatChoViTris.Where(v => v.MaViTri == cleanId))
                {
                    detail.TrangThai = "OCCUPIED";
                    detail.ThoiGianVaoO = now;
                }

                _db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    MaDon = activeOrder.MaDon,
                    LoaiSuKien = "VEHICLE_PARKED",
                    NguonSuKien = source,
                    MucDo = "INFO",
                    NoiDung = $"Xe đã đỗ an toàn vào ô {cleanId}. Đơn {activeOrder.MaDon} chuyển sang PARKED."
                });

                string prefix = Policy.MqttTopicPrefix ?? "dacn-smartparking/lot1";
                await _mqtt.PublishAsync($"{prefix}/slot/{cleanId}/cmd", "RED", retain: false);

                await _db.SaveChangesAsync();

                // Khi xe đỗ thành công -> Số xe ENTERED giảm -> Kiểm tra tự mở lại cổng vào nếu đang HOLD
                await CheckAndAutoReopenGateAsync();

                if (_notifier != null)
                {
                    await _notifier.NotifyOrderStatusChangedAsync(activeOrder.MaDon, "PARKED", $"Xe đã vào ô {cleanId}");
                }
            }
            else
            {
                // Phát hiện đỗ chui nếu không có đơn hợp lệ
                _db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    LoaiSuKien = "UNAUTHORIZED_PARKING",
                    NguonSuKien = source,
                    MucDo = "WARNING",
                    NoiDung = $"Cảnh báo: Phát hiện xe đỗ tại ô {cleanId} nhưng không có đơn đặt chỗ hợp lệ."
                });

                if (_notifier != null)
                {
                    await _notifier.NotifyAlertAsync("UNAUTHORIZED_PARKING", $"Phát hiện xe đỗ trái phép tại ô {cleanId}.", new { slot = cleanId });
                }

                await _db.SaveChangesAsync();
            }
        }
        else if (newStatus == "FREE" && oldStatus == "OCCUPIED")
        {
            string prefix = Policy.MqttTopicPrefix ?? "dacn-smartparking/lot1";
            await _mqtt.PublishAsync($"{prefix}/slot/{cleanId}/cmd", "GREEN", retain: false);
            await _db.SaveChangesAsync();
        }
        else
        {
            await _db.SaveChangesAsync();
        }

        if (_notifier != null)
        {
            await _notifier.NotifySlotChangedAsync(slot.MaViTri, newStatus, (decimal)distanceCm);
            await _notifier.NotifySlotsReloadAsync();
        }

        return slot;
    }

    private async Task RecordScanLogAsync(
        string entryCode,
        long? orderId,
        string gateType,
        string plate,
        string result,
        string reason)
    {
        _db.NhatKyQuetMas.Add(new NhatKyQuetMa
        {
            MaEntryCode = entryCode,
            MaDon = orderId,
            LoaiCong = gateType,
            BienSoQuet = plate,
            KetQua = result,
            LyDo = reason,
            ThoiGianQuet = CurrentTime
        });
        await _db.SaveChangesAsync();
    }
}
