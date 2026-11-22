using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Dtos;
using SmartParking.Api.Models;

namespace SmartParking.Api.Services;

public class ParkingOrderService
{
    private readonly SmartParkingDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<ParkingPolicySettings> _policyOptions;
    private readonly ILogger<ParkingOrderService> _logger;

    public ParkingOrderService(
        SmartParkingDbContext db,
        TimeProvider timeProvider,
        IOptionsMonitor<ParkingPolicySettings> policyOptions,
        ILogger<ParkingOrderService> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _policyOptions = policyOptions;
        _logger = logger;
    }

    private ParkingPolicySettings Policy => _policyOptions.CurrentValue;

    private DateTime CurrentTime => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Đặt chỗ giữ ô (HELD) 5 phút, kiểm tra chồng lấn khung giờ, buffer gap và BookableRatio.
    /// Khóa ô bằng Transaction Serializable để chống 2 khách giữ cùng ô.
    /// </summary>
    public async Task<BookingResponseDto> CreateBookingAsync(int customerId, CreateBookingRequest request)
    {
        if (request.DanhSachViTri == null || request.DanhSachViTri.Count == 0)
        {
            throw new ArgumentException("Danh sách vị trí đỗ không được để trống.");
        }

        if (request.GoiThoiLuongGio != 1 && request.GoiThoiLuongGio != 2 && request.GoiThoiLuongGio != 4)
        {
            throw new ArgumentException("Gói thời lượng không hợp lệ. Chỉ chấp nhận gói 1h, 2h hoặc 4h.");
        }

        DateTime now = CurrentTime;
        DateTime henT = request.ThoiGianHenT.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(request.ThoiGianHenT, DateTimeKind.Utc)
            : request.ThoiGianHenT.ToUniversalTime();

        // 1. Kiểm tra giờ hẹn trong quá khứ
        if (henT < now.AddMinutes(-1))
        {
            throw new ArgumentException("Giờ hẹn đặt chỗ không được ở trong quá khứ.");
        }

        DateTime ketThuc = henT.AddHours(request.GoiThoiLuongGio);

        // 2. Kiểm tra giờ hoạt động của bãi đỗ xe (múi giờ Việt Nam UTC+7)
        DateTime localStart = henT.AddHours(7);
        DateTime localEnd = ketThuc.AddHours(7);

        if (!Policy.DemoMode && (localStart.Hour < Policy.OpenHour || (localStart.Hour >= Policy.CloseHour && localStart.Minute > 0) || localStart.Hour > Policy.CloseHour ||
            localEnd.Hour > Policy.CloseHour || (localEnd.Hour == Policy.CloseHour && localEnd.Minute > 0)))
        {
            throw new ArgumentException(
                $"Bãi xe chỉ hoạt động từ {Policy.OpenHour:D2}:00 đến {Policy.CloseHour:D2}:00 (giờ Việt Nam UTC+7). " +
                $"Khung giờ đặt ({localStart:HH:mm} - {localEnd:HH:mm}) nằm ngoài giờ hoạt động.");
        }

        TimeSpan gap = TimeSpan.FromMinutes(Policy.MinGapBetweenBookingsMin);
        DateTime bufferedStart = henT - gap;
        DateTime bufferedEnd = ketThuc + gap;

        var slotIds = request.DanhSachViTri
            .Select(x => x.MaViTri.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        if (slotIds.Count != request.DanhSachViTri.Count)
        {
            throw new ArgumentException("Không được chọn trùng vị trí đỗ trong cùng một đơn.");
        }

        // Bắt đầu Transaction mức Serializable để cô lập và chống race condition giữa các yêu cầu đặt chỗ đồng thời
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        // 9. Mã giao dịch cryptographically random, dài 64 hex, không chứa thông tin đoán được
        string idempotencyKey = $"TXN-{RandomNumberGenerator.GetHexString(32).ToUpperInvariant()}";

        try
        {
            // 3. Khóa hàng vị trí đỗ ngay từ đầu với UPDLOCK, HOLDLOCK trên SQL Server để chống race condition
            List<ViTriDoXe> targetSlots;
            if (_db.Database.IsSqlServer())
            {
                targetSlots = new List<ViTriDoXe>();
                foreach (var sId in slotIds)
                {
                    var s = await _db.ViTriDoXes
                        .FromSqlInterpolated($"SELECT * FROM dbo.ViTriDoXe WITH (UPDLOCK, HOLDLOCK) WHERE MaViTri = {sId}")
                        .FirstOrDefaultAsync();
                    if (s != null) targetSlots.Add(s);
                }
            }
            else
            {
                targetSlots = await _db.ViTriDoXes
                    .Where(v => slotIds.Contains(v.MaViTri))
                    .ToListAsync();
            }

            if (targetSlots.Count != slotIds.Count)
            {
                throw new ArgumentException("Một hoặc nhiều vị trí đỗ không tồn tại trong hệ thống.");
            }

            foreach (var slot in targetSlots)
            {
                if (slot.TrangThai == "MAINTENANCE")
                {
                    throw new InvalidOperationException($"Vị trí {slot.MaViTri} đang bảo trì, không thể đặt chỗ.");
                }

                // Nếu giờ hẹn là ngay thời điểm hiện tại mà ô đang bận hoặc đang được giữ
                if (Math.Abs((henT - now).TotalMinutes) <= Policy.CheckInGraceMin)
                {
                    if (slot.TrangThai == "HELD" || slot.TrangThai == "OCCUPIED" || slot.TrangThai == "GUIDING")
                    {
                        throw new InvalidOperationException($"Vị trí {slot.MaViTri} hiện tại đang bận hoặc đang được khách khác giữ.");
                    }
                }
            }

            // 4. Kiểm tra chồng lấn khung giờ với khoảng đệm MinGapBetweenBookingsMin
            var conflictingDetails = await _db.DonDatChoViTris
                .Include(ct => ct.DonDatCho)
                .Where(ct => slotIds.Contains(ct.MaViTri))
                .Where(ct => ct.DonDatCho.TrangThai != "CANCELLED" &&
                             ct.DonDatCho.TrangThai != "EXPIRED" &&
                             ct.DonDatCho.TrangThai != "NO_SHOW" &&
                             ct.DonDatCho.TrangThai != "COMPLETED" &&
                             ct.DonDatCho.TrangThai != "ABANDONED")
                .Where(ct => ct.DonDatCho.ThoiGianHenT < bufferedEnd &&
                             ct.DonDatCho.ThoiGianKetThucDuKien > bufferedStart)
                .ToListAsync();

            if (conflictingDetails.Count > 0)
            {
                var conflict = conflictingDetails.First();
                throw new InvalidOperationException(
                    $"Vị trí {conflict.MaViTri} đã có lịch đặt từ {conflict.DonDatCho.ThoiGianHenT:HH:mm} đến {conflict.DonDatCho.ThoiGianKetThucDuKien:HH:mm} " +
                    $"(bao gồm khoảng đệm {Policy.MinGapBetweenBookingsMin} phút). Vui lòng chọn khung giờ hoặc vị trí khác.");
            }

            // 5. Kiểm tra BookableRatio
            int totalSlotsCount = await _db.ViTriDoXes.AsNoTracking().CountAsync();
            if (totalSlotsCount == 0) totalSlotsCount = 40;
            int maxBookable = (int)Math.Floor(totalSlotsCount * Policy.BookableRatio);

            int activeBookingsInWindow = await _db.DonDatChos
                .Where(d => d.TrangThai != "CANCELLED" && d.TrangThai != "EXPIRED" && d.TrangThai != "NO_SHOW" && d.TrangThai != "COMPLETED" && d.TrangThai != "ABANDONED")
                .Where(d => d.ThoiGianHenT < bufferedEnd && d.ThoiGianKetThucDuKien > bufferedStart)
                .SelectMany(d => d.DonDatChoViTris)
                .CountAsync();

            if (activeBookingsInWindow + slotIds.Count > maxBookable && request.LoaiKhach == "DAT_TRUOC")
            {
                throw new InvalidOperationException(
                    $"Đã vượt quá tỷ lệ ô tối đa cho phép đặt trước ({Policy.BookableRatio:P0} = {maxBookable} ô). " +
                    $"Các ô còn lại được ưu tiên dành riêng cho khách vãng lai.");
            }

            // 6. Khóa ô: Chuyển trạng thái sang HELD (nếu khung giờ sát hiện tại) và cập nhật thời gian
            foreach (var slot in targetSlots)
            {
                if ((henT - now).TotalMinutes <= Policy.CheckInGraceMin)
                {
                    slot.TrangThai = "HELD";
                }
                slot.LanCapNhat = now;
            }

            // 7. Tạo đơn đặt chỗ
            decimal tongTien = Policy.PricePerHour * request.GoiThoiLuongGio * slotIds.Count;
            DateTime hanThanhToan = now.AddMinutes(Policy.HoldMin);
            DateTime hanCheckIn = henT.AddMinutes(Policy.CheckInGraceMin);

            var don = new DonDatCho
            {
                MaTaiKhoan = customerId,
                LoaiKhach = request.LoaiKhach,
                ThoiGianDat = now,
                ThoiGianHenT = henT,
                GoiThoiLuongGio = request.GoiThoiLuongGio,
                ThoiGianKetThucDuKien = ketThuc,
                SoLuongXe = slotIds.Count,
                TongTien = tongTien,
                TrangThai = "PENDING_PAYMENT",
                HanThanhToan = hanThanhToan,
                HanCheckIn = hanCheckIn,
                GhiChu = request.GhiChu
            };

            _db.DonDatChos.Add(don);
            await _db.SaveChangesAsync();

            // 8. Tạo chi tiết vị trí và biển số
            foreach (var item in request.DanhSachViTri)
            {
                var detail = new DonDatChoViTri
                {
                    MaDon = don.MaDon,
                    MaViTri = item.MaViTri.Trim().ToUpperInvariant(),
                    BienSo = (item.BienSo ?? "").Trim().ToUpperInvariant(),
                    TrangThai = "ASSIGNED"
                };
                _db.DonDatChoViTris.Add(detail);
            }

            var thanhToan = new ThanhToan
            {
                MaDon = don.MaDon,
                HinhThuc = "CHUYEN_KHOAN",
                SoTien = tongTien,
                TrangThai = "PENDING",
                MaGiaoDichDuyNhat = idempotencyKey,
                ThoiGianTao = now
            };
            _db.ThanhToans.Add(thanhToan);

            // Ghi nhật ký hệ thống
            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                MaDon = don.MaDon,
                LoaiSuKien = "BOOKING_CREATED",
                NguonSuKien = "BACKEND",
                MucDo = "INFO",
                NoiDung = $"Khách hàng {customerId} giữ {slotIds.Count} ô [{string.Join(", ", slotIds)}] từ {henT:HH:mm} đến {ketThuc:HH:mm}. Hạn thanh toán: {hanThanhToan:HH:mm:ss}."
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return new BookingResponseDto
            {
                MaDon = don.MaDon,
                MaTaiKhoan = don.MaTaiKhoan,
                LoaiKhach = don.LoaiKhach,
                ThoiGianHenT = don.ThoiGianHenT,
                GoiThoiLuongGio = don.GoiThoiLuongGio,
                ThoiGianKetThucDuKien = don.ThoiGianKetThucDuKien,
                SoLuongXe = don.SoLuongXe,
                TongTien = don.TongTien,
                TrangThai = don.TrangThai,
                HanThanhToan = don.HanThanhToan,
                HanCheckIn = don.HanCheckIn,
                MaGiaoDichDuyNhat = idempotencyKey,
                DanhSachViTri = slotIds
            };
        }
        catch (Exception ex) when (IsConcurrencyOrConflictException(ex))
        {
            try { await tx.RollbackAsync(); } catch { }
            _logger.LogWarning(ex, "Lỗi xung đột khi tạo đơn đặt chỗ: {Message}", ex.Message);
            throw new InvalidOperationException(
                "Xung đột đặt chỗ: Vị trí đỗ xe đã được giữ hoặc đặt bởi khách hàng khác trong cùng khung giờ. Vui lòng chọn vị trí khác.");
        }
    }

    /// <summary>
    /// Hàm xử lý thanh toán dùng chung: so khớp số tiền, từ chối EXPIRED/CANCELLED, Idempotent,
    /// ghi nhận bảng ThanhToan và đổi trạng thái đơn hàng sang PAID trong cùng một Transaction.
    /// </summary>
    public async Task<BookingResponseDto> ProcessPaymentSuccessAsync(
        long orderId,
        decimal soTien,
        string hinhThuc,
        string? maGiaoDichNgoai,
        string? ghiChu)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .FirstOrDefaultAsync(d => d.MaDon == orderId);

        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        // Idempotency: Nếu đơn đã thanh toán PAID trước đó
        if (don.TrangThai == "PAID")
        {
            var existingTx = don.ThanhToans.FirstOrDefault(t => t.TrangThai == "PAID");
            if (existingTx != null && (existingTx.HinhThuc == hinhThuc || hinhThuc.StartsWith("CHUYEN_KHOAN")))
            {
                _logger.LogInformation("Đơn hàng {OrderId} đã được thanh toán trước đó (Idempotent).", orderId);
                await tx.RollbackAsync();
                return MapToDto(don);
            }
            throw new InvalidOperationException("Đơn hàng đã được thanh toán bằng phương thức khác.");
        }

        if (don.TrangThai == "EXPIRED")
        {
            throw new InvalidOperationException("Đơn hàng đã hết hạn thanh toán (EXPIRED).");
        }

        if (don.TrangThai == "CANCELLED")
        {
            throw new InvalidOperationException("Đơn hàng đã bị hủy (CANCELLED).");
        }

        if (don.TrangThai != "PENDING_PAYMENT")
        {
            throw new InvalidOperationException($"Không thể thanh toán đơn hàng ở trạng thái {don.TrangThai}.");
        }

        // So sánh số tiền thanh toán phải khớp chính xác với tổng tiền đơn hàng
        if (soTien != don.TongTien)
        {
            throw new ArgumentException(
                $"Số tiền thanh toán ({soTien:#,##0} VND) không khớp với tổng tiền của đơn hàng ({don.TongTien:#,##0} VND).");
        }

        DateTime now = CurrentTime;

        // Cập nhật hoặc tạo bản ghi ThanhToan
        var thanhToan = don.ThanhToans.FirstOrDefault();
        if (thanhToan == null)
        {
            thanhToan = new ThanhToan
            {
                MaDon = don.MaDon,
                SoTien = soTien,
                ThoiGianTao = now
            };
            _db.ThanhToans.Add(thanhToan);
        }

        thanhToan.HinhThuc = hinhThuc;
        thanhToan.SoTien = soTien;
        thanhToan.TrangThai = "PAID";
        thanhToan.ThoiGianThanhToan = now;
        if (!string.IsNullOrWhiteSpace(maGiaoDichNgoai))
        {
            thanhToan.MaGiaoDichDuyNhat = maGiaoDichNgoai;
        }
        thanhToan.GhiChu = ghiChu ?? "Thanh toán thành công.";

        // Đổi trạng thái đơn hàng sang PAID
        don.TrangThai = "PAID";

        // Cập nhật trạng thái các ô đỗ
        var slotIds = don.DonDatChoViTris.Select(x => x.MaViTri).ToList();
        var slots = await _db.ViTriDoXes.Where(s => slotIds.Contains(s.MaViTri)).ToListAsync();

        foreach (var slot in slots)
        {
            if (don.ThoiGianHenT > now.AddMinutes(Policy.CheckInGraceMin))
            {
                slot.TrangThai = "BOOKED_FUTURE";
            }
            else
            {
                slot.TrangThai = "BOOKED";
            }
            slot.LanCapNhat = now;
        }

        // Sinh EntryCode mở cổng cho từng xe (nếu chưa có)
        if (!don.MaVaoCongs.Any())
        {
            foreach (var detail in don.DonDatChoViTris)
            {
                var entryCode = new MaVaoCong
                {
                    MaEntryCode = $"QR-{don.MaDon}-{detail.MaViTri}-{RandomNumberGenerator.GetHexString(4).ToUpperInvariant()}",
                    MaDon = don.MaDon,
                    BienSo = detail.BienSo,
                    HanSuDung = don.HanCheckIn ?? don.ThoiGianHenT.AddMinutes(Policy.CheckInGraceMin),
                    TrangThai = "ACTIVE",
                    NgayTao = now
                };
                _db.MaVaoCongs.Add(entryCode);
            }
        }

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "PAYMENT_CONFIRMED",
            NguonSuKien = "BACKEND",
            MucDo = "INFO",
            NoiDung = $"Đã xác nhận thanh toán thành công ({hinhThuc}) cho đơn {don.MaDon}. Số tiền: {soTien:#,##0} VND."
        });

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync();
            var fresh = await _db.DonDatChos.AsNoTracking().FirstOrDefaultAsync(d => d.MaDon == orderId);
            if (fresh != null && fresh.TrangThai == "EXPIRED")
            {
                throw new InvalidOperationException("Đơn hàng đã hết hạn thanh toán (EXPIRED) do quá thời gian chờ.");
            }
            throw;
        }

        return MapToDto(don);
    }

    /// <summary>
    /// Xử lý Webhook ngân hàng thật: bắt buộc chữ ký HMAC-SHA256 (header X-Signature), chống replay bằng X-Timestamp.
    /// </summary>
    public async Task<BookingResponseDto> ConfirmBankTransferWithHmacAsync(
        BankTransferWebhookRequest request,
        string? signatureHeader,
        string? timestampHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(timestampHeader))
        {
            throw new UnauthorizedAccessException("Thiếu header chữ ký số X-Signature hoặc X-Timestamp.");
        }

        if (!long.TryParse(timestampHeader, out long tsEpoch))
        {
            throw new UnauthorizedAccessException("Header X-Timestamp không đúng định dạng Unix timestamp.");
        }

        // Chống replay attack: chênh lệch thời gian tối đa 5 phút (300 giây)
        DateTime tsUtc = DateTimeOffset.FromUnixTimeSeconds(tsEpoch).UtcDateTime;
        if (Math.Abs((CurrentTime - tsUtc).TotalSeconds) > 300)
        {
            throw new UnauthorizedAccessException("Yêu cầu Webhook đã hết hạn thời gian (nguy cơ replay attack).");
        }

        // Chuẩn hóa dữ liệu ký: {timestamp}.{maGiaoDichDuyNhat}.{soTien}
        string canonicalData = $"{timestampHeader}.{request.MaGiaoDichDuyNhat}.{Convert.ToInt64(request.SoTien)}";
        byte[] secretBytes = Encoding.UTF8.GetBytes(Policy.WebhookSecret);
        byte[] expectedHash = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes(canonicalData));
        string expectedHex = Convert.ToHexString(expectedHash).ToLowerInvariant();

        byte[] actualSigBytes = Encoding.UTF8.GetBytes(signatureHeader.Trim().ToLowerInvariant());
        byte[] expectedSigBytes = Encoding.UTF8.GetBytes(expectedHex);

        if (actualSigBytes.Length != expectedSigBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(actualSigBytes, expectedSigBytes))
        {
            throw new UnauthorizedAccessException("Chữ ký số HMAC-SHA256 (X-Signature) không hợp lệ.");
        }

        // Tìm giao dịch theo mã duy nhất
        var thanhToan = await _db.ThanhToans.FirstOrDefaultAsync(t => t.MaGiaoDichDuyNhat == request.MaGiaoDichDuyNhat);
        if (thanhToan == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy giao dịch với mã {request.MaGiaoDichDuyNhat}.");
        }

        return await ProcessPaymentSuccessAsync(
            thanhToan.MaDon,
            request.SoTien,
            "CHUYEN_KHOAN",
            request.MaGiaoDichDuyNhat,
            request.NoiDungChuyenKhoan ?? "Thanh toán chuyển khoản ngân hàng qua Webhook có chữ ký số HMAC-SHA256");
    }

    /// <summary>
    /// Phương thức tương thích ngược cho Webhook mô phỏng (gọi nội bộ hoặc demo).
    /// </summary>
    public async Task<BookingResponseDto> ConfirmBankTransferAsync(BankTransferWebhookRequest request)
    {
        if (!Policy.DemoMode)
        {
            throw new InvalidOperationException("Chức năng mô phỏng thanh toán trực tiếp chỉ được phép khi DemoMode = true.");
        }

        if (string.IsNullOrWhiteSpace(request.MaGiaoDichDuyNhat))
        {
            throw new ArgumentException("Mã giao dịch duy nhất không được để trống.");
        }

        var thanhToan = await _db.ThanhToans.FirstOrDefaultAsync(t => t.MaGiaoDichDuyNhat == request.MaGiaoDichDuyNhat);
        if (thanhToan == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy giao dịch với mã {request.MaGiaoDichDuyNhat}.");
        }

        return await ProcessPaymentSuccessAsync(
            thanhToan.MaDon,
            request.SoTien,
            "CHUYEN_KHOAN_DEMO",
            request.MaGiaoDichDuyNhat,
            request.NoiDungChuyenKhoan ?? "Mô phỏng thanh toán chuyển khoản thành công");
    }

    /// <summary>
    /// Endpoint mô phỏng thanh toán dành riêng cho Demo (gated by DemoMode == true).
    /// Yêu cầu JWT và kiểm tra quyền sở hữu đơn hàng (Customer chỉ gọi được đơn của chính mình).
    /// </summary>
    public async Task<BookingResponseDto> SimulateDemoPaymentAsync(long orderId, int currentUserId, string role)
    {
        if (!Policy.DemoMode)
        {
            // Trả lỗi dạng KeyNotFoundException để controller phản hồi 404 Not Found theo yêu cầu
            throw new KeyNotFoundException("Endpoint demo không khả dụng khi DemoMode = false.");
        }

        var don = await _db.DonDatChos.FindAsync(orderId);
        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        if (role == "Customer" && don.MaTaiKhoan != currentUserId)
        {
            // Trả 404 Not Found để triệt tiêu nguy cơ Broken Object Level Authorization (BOLA) ID enumeration
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        return await ProcessPaymentSuccessAsync(
            orderId,
            don.TongTien,
            "CHUYEN_KHOAN",
            $"DEMO-TXN-{RandomNumberGenerator.GetHexString(16).ToUpperInvariant()}",
            "Mô phỏng thanh toán chuyển khoản thành công trong chế độ DemoMode");
    }

    private static readonly char[] CashCodeCharset = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();

    public static string GenerateReadableCashCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            chars[i] = CashCodeCharset[bytes[i] % CashCodeCharset.Length];
        }
        return new string(chars);
    }

    /// <summary>
    /// Nhân viên Staff xác nhận đã nhận tiền mặt và sinh CashCode.
    /// CashCode được băm HMAC-SHA256 với khóa server trước khi lưu vào DB (không lưu plain-text), gắn với đúng đơn và đúng khách.
    /// Khóa chính bảng MaTienMat là MaDon (1-1 với DonDatCho).
    /// </summary>
    public async Task<string> StaffCreateCashCodeAsync(long orderId, int staffId)
    {
        var staff = await _db.TaiKhoans.FindAsync(staffId);
        if (staff == null || (staff.VaiTro != "Staff" && staff.VaiTro != "Admin"))
        {
            throw new UnauthorizedAccessException("Chỉ nhân viên trực bãi (Staff) hoặc Admin mới có quyền tạo CashCode.");
        }

        var don = await _db.DonDatChos.FindAsync(orderId);
        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        if (don.TrangThai != "PENDING_PAYMENT")
        {
            throw new InvalidOperationException($"Đơn hàng {orderId} đang ở trạng thái {don.TrangThai}, không thể tạo mã thu tiền mặt.");
        }

        DateTime now = CurrentTime;
        string plainCashCode = GenerateReadableCashCode();
        byte[] secretBytes = Encoding.UTF8.GetBytes(Policy.WebhookSecret);
        byte[] hashBytes = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes(plainCashCode));
        string cashCodeHash = Convert.ToHexString(hashBytes).ToUpperInvariant();

        var existing = await _db.MaTienMats.FindAsync(orderId);
        if (existing != null)
        {
            existing.MaCashCodeHash = cashCodeHash;
            existing.MaStaff = staffId;
            existing.MaTaiKhoanCustomer = don.MaTaiKhoan;
            existing.HanSuDung = now.AddMinutes(Policy.CashCodeExpiryMin);
            existing.DaSuDung = false;
            existing.SoLanNhapSai = 0;
            existing.BiKhoa = false;
            existing.NgayTao = now;
        }
        else
        {
            var cashCode = new MaTienMat
            {
                MaDon = orderId,
                MaCashCodeHash = cashCodeHash,
                MaStaff = staffId,
                MaTaiKhoanCustomer = don.MaTaiKhoan,
                HanSuDung = now.AddMinutes(Policy.CashCodeExpiryMin),
                DaSuDung = false,
                SoLanNhapSai = 0,
                BiKhoa = false,
                NgayTao = now
            };
            _db.MaTienMats.Add(cashCode);
        }

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = orderId,
            LoaiSuKien = "CASH_CODE_GENERATED",
            NguonSuKien = "STAFF",
            MucDo = "INFO",
            NoiDung = $"Nhân viên {staff.TenDangNhap} (ID {staffId}) đã sinh CashCode cho đơn {orderId}. Hạn dùng: {now.AddMinutes(Policy.CashCodeExpiryMin):HH:mm:ss}."
        });

        await _db.SaveChangesAsync();
        return plainCashCode;
    }

    /// <summary>
    /// Khách hàng nhập CashCode để kích hoạt thanh toán tiền mặt.
    /// Kiểm tra: quyền sở hữu (Customer chỉ gọi đơn của mình), băm HMAC-SHA256 so sánh hằng thời gian, số lần thử sai (khóa sau 5 lần).
    /// </summary>
    public async Task<BookingResponseDto> RedeemCashCodeAsync(RedeemCashCodeRequest request, int currentUserId, string role)
    {
        if (string.IsNullOrWhiteSpace(request.MaCashCode))
        {
            throw new ArgumentException("Mã tiền mặt không được để trống.");
        }

        var don = await _db.DonDatChos.FindAsync(request.MaDon);
        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {request.MaDon}.");
        }

        // Quyền sở hữu: Customer chỉ được thao tác đơn của chính mình
        if (role == "Customer" && don.MaTaiKhoan != currentUserId)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {request.MaDon}.");
        }

        var cashRecord = await _db.MaTienMats.FindAsync(request.MaDon);

        if (cashRecord == null)
        {
            throw new ArgumentException("Đơn hàng này chưa có mã thu tiền mặt nào được phát hành.");
        }

        if (cashRecord.MaTaiKhoanCustomer != don.MaTaiKhoan)
        {
            throw new UnauthorizedAccessException("Mã tiền mặt không thuộc về khách hàng này.");
        }

        if (cashRecord.BiKhoa)
        {
            throw new InvalidOperationException("Mã tiền mặt đã bị khóa do nhập sai quá 5 lần. Vui lòng liên hệ nhân viên bãi xe.");
        }

        // So sánh băm HMAC-SHA256 bằng hằng thời gian (constant-time)
        string cleanInput = (request.MaCashCode ?? "").Trim().ToUpperInvariant();
        if (cleanInput.StartsWith("CASH-"))
        {
            cleanInput = cleanInput.Substring(5);
        }

        byte[] secretBytes = Encoding.UTF8.GetBytes(Policy.WebhookSecret);
        byte[] inputHash = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes(cleanInput));
        byte[] storedHash = Convert.FromHexString(cashRecord.MaCashCodeHash);

        bool isMatch = (inputHash.Length == storedHash.Length) && CryptographicOperations.FixedTimeEquals(inputHash, storedHash);

        if (!isMatch)
        {
            cashRecord.SoLanNhapSai++;
            if (cashRecord.SoLanNhapSai >= Policy.CashCodeMaxFailedAttempts)
            {
                cashRecord.BiKhoa = true;
            }
            await _db.SaveChangesAsync();

            if (cashRecord.BiKhoa)
            {
                throw new InvalidOperationException("Mã tiền mặt không chính xác và đã bị khóa do vượt quá 5 lần thử.");
            }

            int remaining = Policy.CashCodeMaxFailedAttempts - cashRecord.SoLanNhapSai;
            throw new ArgumentException($"Mã tiền mặt không chính xác. Bạn còn {remaining} lần thử trước khi mã bị khóa.");
        }

        DateTime now = CurrentTime;

        if (cashRecord.DaSuDung)
        {
            throw new InvalidOperationException("Mã tiền mặt này đã được sử dụng trước đó.");
        }

        if (cashRecord.HanSuDung < now)
        {
            throw new InvalidOperationException("Mã tiền mặt này đã hết hạn sử dụng.");
        }

        // Đánh dấu mã đã sử dụng thành công
        cashRecord.DaSuDung = true;
        cashRecord.ThoiGianSuDung = now;
        await _db.SaveChangesAsync();

        return await ProcessPaymentSuccessAsync(
            don.MaDon,
            don.TongTien,
            "TIEN_MAT",
            $"CASH-{cashRecord.MaCashCodeHash[..16]}",
            $"Thanh toán tiền mặt qua CashCode bởi Staff {cashRecord.MaStaff}");
    }

    /// <summary>
    /// Hủy đơn đặt chỗ khi đang PENDING_PAYMENT: giải phóng ô HELD về FREE, cập nhật trạng thái CANCELLED.
    /// Customer chỉ hủy được đơn của mình; Staff/Admin hủy được mọi đơn.
    /// </summary>
    public async Task<BookingResponseDto> CancelOrderAsync(long orderId, int currentUserId, string role)
    {
        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .FirstOrDefaultAsync(d => d.MaDon == orderId);

        if (don == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        if (role == "Customer" && don.MaTaiKhoan != currentUserId)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");
        }

        if (don.TrangThai != "PENDING_PAYMENT")
        {
            throw new InvalidOperationException($"Không thể hủy đơn hàng ở trạng thái {don.TrangThai}. Chỉ có thể hủy đơn khi đang chờ thanh toán.");
        }

        DateTime now = CurrentTime;
        don.TrangThai = "CANCELLED";

        // Giải phóng các ô đỗ đang HELD về FREE
        var slotIds = don.DonDatChoViTris.Select(v => v.MaViTri).ToList();
        var slots = await _db.ViTriDoXes.Where(s => slotIds.Contains(s.MaViTri) && s.TrangThai == "HELD").ToListAsync();
        foreach (var s in slots)
        {
            s.TrangThai = "FREE";
            s.LanCapNhat = now;
        }

        _db.NhatKyHeThongs.Add(new NhatKyHeThong
        {
            MaDon = don.MaDon,
            LoaiSuKien = "BOOKING_CANCELLED",
            NguonSuKien = "USER",
            MucDo = "INFO",
            NoiDung = $"Đơn hàng {don.MaDon} đã bị hủy bởi người dùng {currentUserId}. Các vị trí [{string.Join(", ", slotIds)}] đã được giải phóng về FREE."
        });

        await _db.SaveChangesAsync();
        return MapToDto(don);
    }

    /// <summary>
    /// Lấy chi tiết đơn đặt chỗ với kiểm soát quyền sở hữu: Customer chỉ xem đơn của mình, Staff/Admin xem mọi đơn.
    /// Nếu Customer cố truy cập đơn của người khác -> trả về null (Controller trả 404 Not Found theo OWASP BOLA).
    /// </summary>
    public async Task<BookingResponseDto?> GetBookingByIdAsync(long orderId, int currentUserId, string role)
    {
        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .FirstOrDefaultAsync(d => d.MaDon == orderId);

        if (don == null)
        {
            return null;
        }

        if (role == "Customer" && don.MaTaiKhoan != currentUserId)
        {
            return null;
        }

        return MapToDto(don);
    }

    public async Task<List<BookingResponseDto>> GetCustomerBookingsAsync(int customerId)
    {
        var dons = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .Include(d => d.MaTienMat)
            .Include(d => d.TaiKhoan)
            .Where(d => d.MaTaiKhoan == customerId)
            .OrderByDescending(d => d.ThoiGianDat)
            .ToListAsync();

        return dons.Select(MapToDto).ToList();
    }

    public async Task<List<BookingResponseDto>> GetStaffBookingsAsync(string? status, string? paymentMethod, int limit = 50)
    {
        var query = _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .Include(d => d.ThanhToans)
            .Include(d => d.MaTienMat)
            .Include(d => d.TaiKhoan)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(d => statuses.Contains(d.TrangThai));
        }

        if (!string.IsNullOrWhiteSpace(paymentMethod))
        {
            string pm = paymentMethod.Trim().ToUpperInvariant();
            if (pm == "CASH" || pm == "TIEN_MAT")
            {
                query = query.Where(d => d.MaTienMat != null || d.ThanhToans.Any(t => t.HinhThuc == "TIEN_MAT"));
            }
            else if (pm == "BANK_TRANSFER" || pm == "CHUYEN_KHOAN")
            {
                query = query.Where(d => d.ThanhToans.Any(t => t.HinhThuc == "CHUYEN_KHOAN"));
            }
        }

        int take = Math.Clamp(limit, 1, 200);
        var dons = await query.OrderByDescending(d => d.ThoiGianDat).Take(take).ToListAsync();
        return dons.Select(MapToDto).ToList();
    }

    public async Task<object> ResolveNoSlotAsync(long orderId, ResolveNoSlotRequest request, string? staffName)
    {
        var don = await _db.DonDatChos
            .Include(d => d.DonDatChoViTris)
            .Include(d => d.MaVaoCongs)
            .FirstOrDefaultAsync(d => d.MaDon == orderId);

        if (don == null) throw new KeyNotFoundException($"Không tìm thấy đơn hàng {orderId}.");

        DateTime now = CurrentTime;
        string action = (request.Action ?? "FREE_EXIT").Trim().ToUpperInvariant();

        if (action == "FREE_EXIT")
        {
            don.TrangThai = "COMPLETED";
            don.ThoiGianRaCong = now;
            don.TienPhat = 0;
            don.GhiChu = $"Nhân viên {staffName} xử lý: Cho xe ra miễn phí do hết ô đỗ ({request.Reason ?? "No slot available"}).";

            var slotIds = don.DonDatChoViTris.Select(v => v.MaViTri).ToList();
            var slots = await _db.ViTriDoXes.Where(s => slotIds.Contains(s.MaViTri)).ToListAsync();
            foreach (var s in slots)
            {
                if (s.TrangThai == "HELD" || s.TrangThai == "GUIDING")
                {
                    s.TrangThai = "FREE";
                    s.LanCapNhat = now;
                }
            }

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                MaDon = don.MaDon,
                LoaiSuKien = "NO_SLOT_FREE_EXIT",
                NguonSuKien = "STAFF",
                MucDo = "WARNING",
                NoiDung = $"Nhân viên {staffName} cho đơn {don.MaDon} ra cổng miễn phí do bãi hết ô trống."
            });

            await _db.SaveChangesAsync();
            return new { success = true, action = "FREE_EXIT", message = $"Đã cho xe đơn {don.MaDon} ra cổng miễn phí." };
        }
        else if (action == "REASSIGN")
        {
            string newSlotId = (request.NewSlotId ?? "").Trim().ToUpperInvariant();
            var freeSlot = await _db.ViTriDoXes.FirstOrDefaultAsync(s => s.MaViTri == newSlotId && s.TrangThai == "FREE");
            if (freeSlot == null)
            {
                throw new InvalidOperationException($"Ô {newSlotId} không tồn tại hoặc không ở trạng thái FREE.");
            }

            var oldLink = don.DonDatChoViTris.FirstOrDefault();
            string oldSlot = oldLink?.MaViTri ?? "";
            if (oldLink != null)
            {
                _db.DonDatChoViTris.Remove(oldLink);
                var oldSlotObj = await _db.ViTriDoXes.FindAsync(oldSlot);
                if (oldSlotObj != null && (oldSlotObj.TrangThai == "HELD" || oldSlotObj.TrangThai == "GUIDING"))
                {
                    oldSlotObj.TrangThai = "FREE";
                    oldSlotObj.LanCapNhat = now;
                }
            }

            _db.DonDatChoViTris.Add(new DonDatChoViTri
            {
                MaDon = don.MaDon,
                MaViTri = freeSlot.MaViTri,
                BienSo = oldLink?.BienSo ?? don.MaVaoCongs.FirstOrDefault()?.BienSo ?? "51H-999.88"
            });

            freeSlot.TrangThai = "GUIDING";
            freeSlot.LanCapNhat = now;
            don.HanDoVaoO = now.AddMinutes(5);

            _db.NhatKyHeThongs.Add(new NhatKyHeThong
            {
                MaDon = don.MaDon,
                LoaiSuKien = "STAFF_SLOT_REASSIGNED",
                NguonSuKien = "STAFF",
                MucDo = "INFO",
                NoiDung = $"Nhân viên {staffName} đổi ô cho đơn {don.MaDon} từ {oldSlot} sang {freeSlot.MaViTri}."
            });

            await _db.SaveChangesAsync();
            return new { success = true, action = "REASSIGN", newSlotId = freeSlot.MaViTri, message = $"Đã chuyển ô đỗ thành công sang {freeSlot.MaViTri}." };
        }

        throw new ArgumentException("Hành động không hợp lệ.");
    }

    public static BookingResponseDto MapToDto(DonDatCho don)
    {
        var tt = don.ThanhToans.FirstOrDefault();
        string phuongThuc = tt?.HinhThuc ?? (don.MaTienMat != null ? "TIEN_MAT" : "CHUYEN_KHOAN");
        string? cashCode = don.MaTienMat != null ? (don.MaTienMat.DaSuDung ? "DA_SU_DUNG" : (don.MaTienMat.BiKhoa ? "BI_KHOA" : "CHO_NHAP")) : null;
        string? bienSo = don.DonDatChoViTris.FirstOrDefault()?.BienSo ?? don.MaVaoCongs.FirstOrDefault()?.BienSo;
        string? tenKhach = don.TaiKhoan?.HoTen ?? don.TaiKhoan?.TenDangNhap;

        return new BookingResponseDto
        {
            MaDon = don.MaDon,
            MaTaiKhoan = don.MaTaiKhoan,
            LoaiKhach = don.LoaiKhach,
            ThoiGianHenT = don.ThoiGianHenT,
            GoiThoiLuongGio = don.GoiThoiLuongGio,
            ThoiGianKetThucDuKien = don.ThoiGianKetThucDuKien,
            SoLuongXe = don.SoLuongXe,
            TongTien = don.TongTien,
            TrangThai = don.TrangThai,
            HanThanhToan = don.HanThanhToan,
            HanCheckIn = don.HanCheckIn,
            HanDoVaoO = don.HanDoVaoO,
            SoLanGiaHan = don.SoLanGiaHan,
            MaGiaoDichDuyNhat = tt?.MaGiaoDichDuyNhat,
            DanhSachViTri = don.DonDatChoViTris.Select(x => x.MaViTri).ToList(),
            EntryCodes = don.MaVaoCongs.Select(e => new EntryCodeDto
            {
                MaEntryCode = e.MaEntryCode,
                BienSo = e.BienSo,
                HanSuDung = e.HanSuDung,
                TrangThai = e.TrangThai
            }).ToList(),
            PhuongThucThanhToan = phuongThuc,
            CashCode = cashCode,
            BienSo = bienSo,
            TenKhachHang = tenKhach,
            TienPhat = don.TienPhat,
            ThoiGianVaoCong = don.ThoiGianVaoCong,
            ThoiGianRaCong = don.ThoiGianRaCong
        };
    }

    private static bool IsConcurrencyOrConflictException(Exception ex)
    {
        if (ex is DbUpdateConcurrencyException or DbUpdateException) return true;
        var current = ex;
        while (current != null)
        {
            if (current is SqlException sqlEx && (sqlEx.Number == 1205 || sqlEx.Number == 2601 || sqlEx.Number == 2627))
                return true;
            if (current.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("transient failure", StringComparison.OrdinalIgnoreCase))
                return true;
            current = current.InnerException;
        }
        return false;
    }
}
