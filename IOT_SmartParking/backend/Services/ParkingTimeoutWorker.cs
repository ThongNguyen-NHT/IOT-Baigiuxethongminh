using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Models;

namespace SmartParking.Api.Services;

/// <summary>
/// Background worker quét định kỳ và tự động xử lý các đơn hàng hết hạn:
/// 1. Đơn PENDING_PAYMENT quá hạn giữ ô (HoldMin) -> EXPIRED.
/// 2. Đơn PAID quá hạn Check-in (T + 15 phút) mà xe chưa qua cổng -> NO_SHOW, KHÔNG hoàn tiền.
/// - Xử lý từng đơn trong Transaction riêng (lỗi một đơn không làm hỏng cả lô).
/// - Bắt DbUpdateConcurrencyException để chống lỗi đua (race condition) với thanh toán.
/// - Nạp ô theo lô (batch loading) tránh N+1 và re-evaluate trạng thái ô chuẩn xác (không nhả FREE sai nếu có đơn tương lai).
/// </summary>
public class ParkingTimeoutWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<ParkingPolicySettings> _policyOptions;
    private readonly ILogger<ParkingTimeoutWorker> _logger;

    public ParkingTimeoutWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptionsMonitor<ParkingPolicySettings> policyOptions,
        ILogger<ParkingTimeoutWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _policyOptions = policyOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ParkingTimeoutWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTimeoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp ParkingTimeoutWorker.");
            }

            // Chu kỳ quét linh hoạt theo TimeScale
            double scale = Math.Max(0.1, _policyOptions.CurrentValue.TimeScale);
            int delayMs = scale > 5.0 ? 500 : 2000;
            await Task.Delay(delayMs, stoppingToken);
        }

        _logger.LogInformation("ParkingTimeoutWorker đang dừng.");
    }

    public async Task ProcessTimeoutsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();

        // Lấy thời gian từ TimeProvider (hỗ trợ TimeScale và TestTimeProvider ảo)
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        // 1. Quét đơn PENDING_PAYMENT hết hạn giữ chỗ (HoldMin)
        var pendingExpiredOrderIds = await db.DonDatChos
            .Where(d => d.TrangThai == "PENDING_PAYMENT" && d.HanThanhToan != null && d.HanThanhToan < now)
            .Select(d => d.MaDon)
            .ToListAsync(ct);

        foreach (var orderId in pendingExpiredOrderIds)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var order = await db.DonDatChos
                    .Include(d => d.DonDatChoViTris)
                    .FirstOrDefaultAsync(d => d.MaDon == orderId, ct);

                // Nếu đơn không còn ở PENDING_PAYMENT (ví dụ khách vừa thanh toán thành công thành PAID)
                if (order == null || order.TrangThai != "PENDING_PAYMENT")
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }

                order.TrangThai = "EXPIRED";
                await db.SaveChangesAsync(ct);

                // Đánh giá lại trạng thái các ô đỗ liên quan
                var slotIds = order.DonDatChoViTris.Select(x => x.MaViTri).ToList();
                await ReevaluateSlotsStatusBatchAsync(db, slotIds, now, _policyOptions.CurrentValue.CheckInGraceMin, ct);

                db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    MaDon = order.MaDon,
                    LoaiSuKien = "ORDER_EXPIRED",
                    NguonSuKien = "BACKEND",
                    MucDo = "WARNING",
                    NoiDung = $"Đơn hàng {order.MaDon} hết hạn giữ chỗ (HoldMin). Đã chuyển sang EXPIRED."
                });

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning("Xung đột concurrency trên đơn {OrderId} khi chuyển EXPIRED. Bỏ qua.", orderId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogError(ex, "Lỗi khi xử lý hết hạn cho đơn {OrderId}.", orderId);
            }
        }

        // 2. Quét đơn PAID quá hạn Check-in (T + 15 phút) mà xe chưa qua cổng
        var noShowOrderIds = await db.DonDatChos
            .Where(d => d.TrangThai == "PAID" && d.HanCheckIn != null && d.HanCheckIn < now)
            .Select(d => d.MaDon)
            .ToListAsync(ct);

        foreach (var orderId in noShowOrderIds)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var order = await db.DonDatChos
                    .Include(d => d.DonDatChoViTris)
                    .Include(d => d.MaVaoCongs)
                    .FirstOrDefaultAsync(d => d.MaDon == orderId, ct);

                if (order == null || order.TrangThai != "PAID")
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }

                order.TrangThai = "NO_SHOW";

                // Vô hiệu hóa các EntryCode đã cấp
                foreach (var code in order.MaVaoCongs)
                {
                    if (code.TrangThai == "ACTIVE")
                    {
                        code.TrangThai = "EXPIRED";
                    }
                }
                await db.SaveChangesAsync(ct);

                // Đánh giá lại trạng thái các ô đỗ liên quan
                var slotIds = order.DonDatChoViTris.Select(x => x.MaViTri).ToList();
                await ReevaluateSlotsStatusBatchAsync(db, slotIds, now, _policyOptions.CurrentValue.CheckInGraceMin, ct);

                db.NhatKyHeThongs.Add(new NhatKyHeThong
                {
                    MaDon = order.MaDon,
                    LoaiSuKien = "ORDER_NO_SHOW",
                    NguonSuKien = "BACKEND",
                    MucDo = "WARNING",
                    NoiDung = $"Đơn hàng {order.MaDon} quá hạn Check-in ({order.HanCheckIn:HH:mm}). Chuyển sang NO_SHOW, KHÔNG hoàn tiền."
                });

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning("Xung đột concurrency trên đơn {OrderId} khi chuyển NO_SHOW. Bỏ qua.", orderId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogError(ex, "Lỗi khi xử lý NO_SHOW cho đơn {OrderId}.", orderId);
            }
        }

        // 3. Quét đơn ENTERED quá hạn tìm ô (HanDoVaoO) mà chưa đỗ xe vào ô
        var enteredExpiredOrderIds = await db.DonDatChos
            .Where(d => d.TrangThai == "ENTERED" && d.HanDoVaoO != null && d.HanDoVaoO < now)
            .Select(d => d.MaDon)
            .ToListAsync(ct);

        foreach (var orderId in enteredExpiredOrderIds)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var order = await db.DonDatChos
                    .Include(d => d.DonDatChoViTris)
                    .FirstOrDefaultAsync(d => d.MaDon == orderId, ct);

                if (order == null || order.TrangThai != "ENTERED")
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }

                var currentSlotId = order.DonDatChoViTris.FirstOrDefault()?.MaViTri;

                // Tìm ô FREE khác trong bãi không bị trùng lịch đặt chỗ
                var activeBookingSlots = await db.DonDatChoViTris
                    .Where(x => x.MaDon != order.MaDon &&
                                x.DonDatCho.TrangThai != "EXPIRED" &&
                                x.DonDatCho.TrangThai != "NO_SHOW" &&
                                x.DonDatCho.TrangThai != "CANCELLED" &&
                                x.DonDatCho.TrangThai != "COMPLETED" &&
                                x.DonDatCho.TrangThai != "ABANDONED" &&
                                x.DonDatCho.ThoiGianKetThucDuKien > now)
                    .Select(x => x.MaViTri)
                    .Distinct()
                    .ToListAsync(ct);

                var freeSlot = await db.ViTriDoXes
                    .Where(s => s.TrangThai == "FREE" && !activeBookingSlots.Contains(s.MaViTri) && s.MaViTri != currentSlotId)
                    .OrderBy(s => s.Hang)
                    .ThenBy(s => s.Cot)
                    .FirstOrDefaultAsync(ct);

                var mqtt = scope.ServiceProvider.GetService<IMqttPublisher>();
                string prefix = _policyOptions.CurrentValue.MqttTopicPrefix ?? "dacn-smartparking/lot1";

                if (freeSlot != null)
                {
                    // Tự đổi sang ô FREE gần nhất
                    string oldSlotId = currentSlotId ?? "";
                    if (order.DonDatChoViTris.Any())
                    {
                        var link = order.DonDatChoViTris.First();
                        link.MaViTri = freeSlot.MaViTri;
                    }

                    int timeoutMin = _policyOptions.CurrentValue.ParkingTimeoutMin > 0 ? _policyOptions.CurrentValue.ParkingTimeoutMin : 10;
                    order.HanDoVaoO = now.AddMinutes(timeoutMin);

                    freeSlot.TrangThai = "GUIDING";
                    freeSlot.LanCapNhat = now;

                    await db.SaveChangesAsync(ct);

                    if (!string.IsNullOrEmpty(oldSlotId))
                    {
                        await ReevaluateSlotsStatusBatchAsync(db, new List<string> { oldSlotId }, now, _policyOptions.CurrentValue.CheckInGraceMin, ct);
                    }

                    db.NhatKyHeThongs.Add(new NhatKyHeThong
                    {
                        MaDon = order.MaDon,
                        LoaiSuKien = "SLOT_REASSIGNED",
                        NguonSuKien = "BACKEND",
                        MucDo = "WARNING",
                        NoiDung = $"Đơn {order.MaDon} quá hạn vào ô ({oldSlotId}). Hệ thống đã tự động chuyển sang ô mới {freeSlot.MaViTri}."
                    });

                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    if (mqtt != null)
                    {
                        await mqtt.PublishAsync($"{prefix}/slot/{freeSlot.MaViTri}/cmd", "GUIDE");
                        await mqtt.PublishAsync($"{prefix}/alert", $"{{\"event\":\"SLOT_REASSIGNED\",\"orderId\":{order.MaDon},\"newSlot\":\"{freeSlot.MaViTri}\"}}");
                    }
                }
                else
                {
                    // Không còn ô FREE nào trong bãi -> Kích hoạt NoSlotPolicy = FreeExit (cho ra miễn phí)
                    order.TrangThai = "COMPLETED";
                    order.ThoiGianRaCong = now;
                    order.TienPhat = 0;
                    order.GhiChu = "NoSlotPolicy=FreeExit: Tự động miễn phí ra cổng do hết ô đỗ trống.";

                    await db.SaveChangesAsync(ct);

                    if (!string.IsNullOrEmpty(currentSlotId))
                    {
                        await ReevaluateSlotsStatusBatchAsync(db, new List<string> { currentSlotId }, now, _policyOptions.CurrentValue.CheckInGraceMin, ct);
                    }

                    db.NhatKyHeThongs.Add(new NhatKyHeThong
                    {
                        MaDon = order.MaDon,
                        LoaiSuKien = "FREE_EXIT_TRIGGERED",
                        NguonSuKien = "BACKEND",
                        MucDo = "WARNING",
                        NoiDung = $"Đơn {order.MaDon} hết hạn tìm ô và bãi đã hết chỗ. Kích hoạt chính sách FreeExit cho xe ra tự do."
                    });

                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    if (mqtt != null)
                    {
                        await mqtt.PublishAsync($"{prefix}/gate/out/cmd", "OPEN");
                        await mqtt.PublishAsync($"{prefix}/alert", $"{{\"event\":\"FREE_EXIT\",\"orderId\":{order.MaDon},\"message\":\"Free exit granted due to full lot\"}}");
                    }
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning("Xung đột concurrency trên đơn {OrderId} khi xử lý timeout vào ô. Bỏ qua.", orderId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogError(ex, "Lỗi khi xử lý timeout vào ô cho đơn {OrderId}.", orderId);
            }
        }
    }

    /// <summary>
    /// Đánh giá lại trạng thái các ô đỗ theo lô (Batch loading):
    /// Chỉ nhả về FREE nếu không còn đơn nào khác đang giữ ô này trong hiện tại hoặc tương lai.
    /// Nếu có đơn tương lai thì hiển thị đúng trạng thái BOOKED_FUTURE, chống nhả nhầm ô.
    /// </summary>
    public static async Task ReevaluateSlotsStatusBatchAsync(
        SmartParkingDbContext db,
        List<string> slotIds,
        DateTime now,
        int graceMin,
        CancellationToken ct = default)
    {
        if (slotIds == null || slotIds.Count == 0) return;

        var slots = await db.ViTriDoXes
            .Where(s => slotIds.Contains(s.MaViTri) && s.TrangThai != "MAINTENANCE")
            .ToListAsync(ct);

        // Nạp theo lô tất cả các đơn đặt chỗ còn hiệu lực trên các ô này (tránh N+1 query)
        var activeBookings = await db.DonDatChoViTris
            .Include(v => v.DonDatCho)
            .Where(v => slotIds.Contains(v.MaViTri))
            .Where(v => v.DonDatCho.TrangThai == "PAID" ||
                        v.DonDatCho.TrangThai == "ENTERED" ||
                        v.DonDatCho.TrangThai == "PARKED" ||
                        v.DonDatCho.TrangThai == "PENDING_PAYMENT")
            .Where(v => v.DonDatCho.ThoiGianKetThucDuKien > now)
            .ToListAsync(ct);

        foreach (var slot in slots)
        {
            var bookingsForSlot = activeBookings
                .Where(b => b.MaViTri == slot.MaViTri)
                .Where(b => b.DonDatCho.TrangThai != "EXPIRED" &&
                            b.DonDatCho.TrangThai != "NO_SHOW" &&
                            b.DonDatCho.TrangThai != "CANCELLED" &&
                            b.DonDatCho.TrangThai != "COMPLETED")
                .OrderBy(b => b.DonDatCho.ThoiGianHenT)
                .ToList();

            if (!bookingsForSlot.Any())
            {
                slot.TrangThai = "FREE";
            }
            else
            {
                var nextOrder = bookingsForSlot.First().DonDatCho;
                if (nextOrder.TrangThai == "PENDING_PAYMENT")
                {
                    slot.TrangThai = (nextOrder.ThoiGianHenT <= now.AddMinutes(graceMin)) ? "HELD" : "FREE";
                }
                else if (nextOrder.TrangThai == "PARKED")
                {
                    slot.TrangThai = "OCCUPIED";
                }
                else if (nextOrder.TrangThai == "ENTERED")
                {
                    slot.TrangThai = "GUIDING";
                }
                else if (nextOrder.TrangThai == "PAID")
                {
                    slot.TrangThai = (nextOrder.ThoiGianHenT <= now.AddMinutes(graceMin)) ? "BOOKED" : "BOOKED_FUTURE";
                }
                else
                {
                    slot.TrangThai = "FREE";
                }
            }
            slot.LanCapNhat = now;
        }
    }
}
