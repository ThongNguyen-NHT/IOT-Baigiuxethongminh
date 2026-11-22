using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartParking.Api.Configuration;
using SmartParking.Api.Controllers;
using SmartParking.Api.Data;
using SmartParking.Api.Dtos;
using SmartParking.Api.Models;
using SmartParking.Api.Services;
using Xunit;

namespace SmartParking.Tests;

public class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    public TestTimeProvider(DateTimeOffset initialTime) => _utcNow = initialTime;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void SetUtcNow(DateTimeOffset time) => _utcNow = time;
    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

public class FakeOptionsMonitor<T> : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; }
    public FakeOptionsMonitor(T currentValue) => CurrentValue = currentValue;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class ParkingPhase2aTests
{
    private const string WebhookSecret = "MySecretWebhookKey_1234567890123456";

    private static SmartParkingDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<SmartParkingDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var db = new SmartParkingDbContext(options);

        // Seed 40 ô đỗ xe (A1-A8, B1-B8, C1-C8, D1-D8, E1-E8)
        if (!db.ViTriDoXes.Any())
        {
            var rows = new[] { "A", "B", "C", "D", "E" };
            foreach (var r in rows)
            {
                for (int c = 1; c <= 8; c++)
                {
                    db.ViTriDoXes.Add(new ViTriDoXe
                    {
                        MaViTri = $"{r}{c}",
                        TenViTri = $"Vị trí {r}{c}",
                        Hang = r,
                        Cot = c,
                        LaViTriAo = r != "E",
                        TrangThai = "FREE"
                    });
                }
            }

            // Seed các tài khoản demo
            db.TaiKhoans.AddRange(
                new TaiKhoan { MaTaiKhoan = 1, TenDangNhap = "admin", HoTen = "Quản trị viên Demo", VaiTro = "Admin", MatKhauHash = "HASH" },
                new TaiKhoan { MaTaiKhoan = 2, TenDangNhap = "staff01", HoTen = "Nhân viên trực bãi Demo", VaiTro = "Staff", MatKhauHash = "HASH" },
                new TaiKhoan { MaTaiKhoan = 3, TenDangNhap = "customer01", HoTen = "Khách hàng Demo 01", VaiTro = "Customer", MatKhauHash = "HASH" },
                new TaiKhoan { MaTaiKhoan = 4, TenDangNhap = "customer02", HoTen = "Khách hàng Demo 02", VaiTro = "Customer", MatKhauHash = "HASH" }
            );

            db.SaveChanges();
        }

        return db;
    }

    private static (string signature, string timestamp) GenerateHmacSignature(string secret, string idempotencyKey, decimal amount, DateTime utcTime)
    {
        long epoch = new DateTimeOffset(utcTime).ToUnixTimeSeconds();
        string timestampStr = epoch.ToString();
        string canonical = $"{timestampStr}.{idempotencyKey}.{Convert.ToInt64(amount)}";
        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(canonical));
        string sig = Convert.ToHexString(hash).ToLowerInvariant();
        return (sig, timestampStr);
    }

    [Fact]
    public async Task Test1_HaiKhachGiuCungMotOCungLuc_TaskWhenAll_ChiMotNguoiThanhCong()
    {
        string dbName = nameof(Test1_HaiKhachGiuCungMotOCungLuc_TaskWhenAll_ChiMotNguoiThanhCong);
        CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policyMonitor = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings { WebhookSecret = WebhookSecret });

        var req1 = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            LoaiKhach = "DAT_TRUOC",
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E7", BienSo = "51A-12345" } }
        };

        var req2 = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            LoaiKhach = "DAT_TRUOC",
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E7", BienSo = "51B-99999" } }
        };

        var db1 = CreateInMemoryDb(dbName);
        var db2 = CreateInMemoryDb(dbName);
        var svc1 = new ParkingOrderService(db1, timeProvider, policyMonitor, NullLogger<ParkingOrderService>.Instance);
        var svc2 = new ParkingOrderService(db2, timeProvider, policyMonitor, NullLogger<ParkingOrderService>.Instance);

        var task1 = svc1.CreateBookingAsync(3, req1);
        var task2 = svc2.CreateBookingAsync(4, req2);

        try
        {
            await Task.WhenAll(task1, task2);
        }
        catch
        {
            // Dự kiến 1 task sẽ thất bại do xung đột giữ ô
        }

        int successCount = 0;
        int conflictCount = 0;

        if (task1.IsCompletedSuccessfully) successCount++; else conflictCount++;
        if (task2.IsCompletedSuccessfully) successCount++; else conflictCount++;

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);
    }

    [Fact]
    public async Task Test2_KiemTraGioHoatDongCuaBai_DatNgoaiGioBiTuChoi()
    {
        var db = CreateInMemoryDb(nameof(Test2_KiemTraGioHoatDongCuaBai_DatNgoaiGioBiTuChoi));
        var baseTime = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { OpenHour = 6, CloseHour = 23, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        // 20:00 UTC = 03:00 UTC+7 (ngoài giờ mở cửa 06:00 - 23:00) -> Bị từ chối
        var reqOutsideHours = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(10), // 20:00 UTC = 03:00 UTC+7 sáng hôm sau
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E2", BienSo = "51A-99999" } }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateBookingAsync(3, reqOutsideHours));
        Assert.Contains("Bãi xe chỉ hoạt động từ 06:00 đến 23:00", ex.Message);
    }

    [Fact]
    public async Task Test3_DatChoGioQuaKhu_BiTuChoi()
    {
        var db = CreateInMemoryDb(nameof(Test3_DatChoGioQuaKhu_BiTuChoi));
        var baseTime = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings { WebhookSecret = WebhookSecret }), NullLogger<ParkingOrderService>.Instance);

        var reqPast = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(-1),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E2", BienSo = "51A-99999" } }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateBookingAsync(3, reqPast));
        Assert.Contains("không được ở trong quá khứ", ex.Message);
    }

    [Fact]
    public async Task Test4_DatChongKhungGioVaKhoangDemMinGap_BiTuChoi()
    {
        var db = CreateInMemoryDb(nameof(Test4_DatChongKhungGioVaKhoangDemMinGap_BiTuChoi));
        var baseTime = new DateTime(2026, 11, 20, 5, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { MinGapBetweenBookingsMin = 15, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var req1 = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(2), // 07:00 UTC = 14:00 UTC+7
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E1", BienSo = "51A-11111" } }
        };
        await service.CreateBookingAsync(3, req1);

        // Chồng lấn trực tiếp
        var reqOverlap = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(2.5),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E1", BienSo = "51B-22222" } }
        };
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBookingAsync(4, reqOverlap));
        Assert.Contains("đã có lịch đặt", ex1.Message);

        // Vi phạm MinGap 15 phút
        var reqGapViolation = new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(2).AddMinutes(70),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E1", BienSo = "51C-33333" } }
        };
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBookingAsync(4, reqGapViolation));
        Assert.Contains("khoảng đệm 15 phút", ex2.Message);
    }

    [Fact]
    public async Task Test5_VuotQuaBookableRatio70PhanTram_BiTuChoi()
    {
        var db = CreateInMemoryDb(nameof(Test5_VuotQuaBookableRatio70PhanTram_BiTuChoi));
        var baseTime = new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { BookableRatio = 0.7, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var slotsBatch1 = new List<BookingSlotItemDto>();
        for (int i = 1; i <= 8; i++) slotsBatch1.Add(new BookingSlotItemDto { MaViTri = $"A{i}", BienSo = $"51A-0000{i}" });
        for (int i = 1; i <= 8; i++) slotsBatch1.Add(new BookingSlotItemDto { MaViTri = $"B{i}", BienSo = $"51B-0000{i}" });
        for (int i = 1; i <= 8; i++) slotsBatch1.Add(new BookingSlotItemDto { MaViTri = $"C{i}", BienSo = $"51C-0000{i}" });
        for (int i = 1; i <= 4; i++) slotsBatch1.Add(new BookingSlotItemDto { MaViTri = $"D{i}", BienSo = $"51D-0000{i}" });

        await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            LoaiKhach = "DAT_TRUOC",
            DanhSachViTri = slotsBatch1
        });

        // Đặt ô thứ 29 -> Bị từ chối
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBookingAsync(4, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            LoaiKhach = "DAT_TRUOC",
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "D5", BienSo = "51D-99999" } }
        }));

        Assert.Contains("vượt quá tỷ lệ ô tối đa cho phép đặt trước", ex.Message);
    }

    [Fact]
    public async Task Test6_DonKhongThanhToanSau5Phut_ExpiredVaNhaO()
    {
        string dbName = nameof(Test6_DonKhongThanhToanSau5Phut_ExpiredVaNhaO);
        var db = CreateInMemoryDb(dbName);
        var baseTime = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { HoldMin = 5, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddMinutes(5),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E3", BienSo = "51A-99999" } }
        });

        var slot = await db.ViTriDoXes.FindAsync("E3");
        Assert.Equal("HELD", slot!.TrangThai);

        // Tua thời gian trôi qua 6 phút bằng đồng hồ giả (KHÔNG dùng Task.Delay)
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new ParkingTimeoutWorker(scopeFactory, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingTimeoutWorker>.Instance);
        await worker.ProcessTimeoutsAsync();

        using var verifyDb = CreateInMemoryDb(dbName);
        var orderAfter = await verifyDb.DonDatChos.FindAsync(booking.MaDon);
        Assert.Equal("EXPIRED", orderAfter!.TrangThai);

        var slotAfter = await verifyDb.ViTriDoXes.FindAsync("E3");
        Assert.Equal("FREE", slotAfter!.TrangThai);
    }

    [Fact]
    public async Task Test7_QuaHanCheckIn15Phut_NoShowKhongHoanTien()
    {
        string dbName = nameof(Test7_QuaHanCheckIn15Phut_NoShowKhongHoanTien);
        var db = CreateInMemoryDb(dbName);
        var baseTime = new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { CheckInGraceMin = 15, DemoMode = true, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E4", BienSo = "51G-88888" } }
        });

        await service.SimulateDemoPaymentAsync(booking.MaDon, 3, "Customer");

        var paidOrder = await db.DonDatChos.FindAsync(booking.MaDon);
        Assert.Equal("PAID", paidOrder!.TrangThai);

        // Tua thời gian qua 11:16 UTC+7 (vượt quá 15 phút CheckInGraceMin, KHÔNG dùng Task.Delay)
        timeProvider.SetUtcNow(baseTime.AddHours(1).AddMinutes(16));

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new ParkingTimeoutWorker(scopeFactory, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingTimeoutWorker>.Instance);
        await worker.ProcessTimeoutsAsync();

        using var verifyDb = CreateInMemoryDb(dbName);
        var noShowOrder = await verifyDb.DonDatChos.FindAsync(booking.MaDon);
        Assert.Equal("NO_SHOW", noShowOrder!.TrangThai);

        var slotAfter = await verifyDb.ViTriDoXes.FindAsync("E4");
        Assert.Equal("FREE", slotAfter!.TrangThai);

        var entryCode = await verifyDb.MaVaoCongs.FirstOrDefaultAsync(c => c.MaDon == booking.MaDon);
        Assert.NotNull(entryCode);
        Assert.Equal("EXPIRED", entryCode.TrangThai);
    }

    [Fact]
    public async Task Test8_WebhookNganHangThat_KiemTraChuKyHmacSoTienVaIdempotent()
    {
        var db = CreateInMemoryDb(nameof(Test8_WebhookNganHangThat_KiemTraChuKyHmacSoTienVaIdempotent));
        var baseTime = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(2),
            GoiThoiLuongGio = 2,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E5", BienSo = "51H-12345" } }
        });

        var (validSig, validTs) = GenerateHmacSignature(policy.WebhookSecret, booking.MaGiaoDichDuyNhat!, booking.TongTien, baseTime);

        // Chữ ký sai -> 401
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ConfirmBankTransferWithHmacAsync(
                new BankTransferWebhookRequest { MaGiaoDichDuyNhat = booking.MaGiaoDichDuyNhat!, SoTien = booking.TongTien },
                "invalid_signature_hex",
                validTs));

        // Timestamp quá hạn 10 phút -> 401
        string expiredTs = new DateTimeOffset(baseTime.AddMinutes(-10)).ToUnixTimeSeconds().ToString();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ConfirmBankTransferWithHmacAsync(
                new BankTransferWebhookRequest { MaGiaoDichDuyNhat = booking.MaGiaoDichDuyNhat!, SoTien = booking.TongTien },
                validSig,
                expiredTs));

        // Sai số tiền -> 400 ArgumentException
        var (sigWrong, tsWrong) = GenerateHmacSignature(policy.WebhookSecret, booking.MaGiaoDichDuyNhat!, booking.TongTien - 5000, baseTime);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ConfirmBankTransferWithHmacAsync(
                new BankTransferWebhookRequest { MaGiaoDichDuyNhat = booking.MaGiaoDichDuyNhat!, SoTien = booking.TongTien - 5000 },
                sigWrong,
                tsWrong));

        // Chữ ký đúng -> Thành công
        var res1 = await service.ConfirmBankTransferWithHmacAsync(
            new BankTransferWebhookRequest { MaGiaoDichDuyNhat = booking.MaGiaoDichDuyNhat!, SoTien = booking.TongTien },
            validSig,
            validTs);
        Assert.Equal("PAID", res1.TrangThai);

        // Lần 2 (Idempotency) -> Trả về thành công ngay, 1 bản ghi duy nhất
        var res2 = await service.ConfirmBankTransferWithHmacAsync(
            new BankTransferWebhookRequest { MaGiaoDichDuyNhat = booking.MaGiaoDichDuyNhat!, SoTien = booking.TongTien },
            validSig,
            validTs);
        Assert.Equal("PAID", res2.TrangThai);
        Assert.Equal(1, await db.ThanhToans.CountAsync(t => t.MaDon == booking.MaDon));
    }

    [Fact]
    public async Task Test9_DemoPaymentEndpoint_DemoModeFalseVaQuyenSoHuu()
    {
        var db = CreateInMemoryDb(nameof(Test9_DemoPaymentEndpoint_DemoModeFalseVaQuyenSoHuu));
        var baseTime = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);

        // DemoMode = false -> 404 KeyNotFoundException
        var policyNoDemo = new ParkingPolicySettings { DemoMode = false, WebhookSecret = WebhookSecret };
        var svcNoDemo = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policyNoDemo), NullLogger<ParkingOrderService>.Instance);

        var booking = await svcNoDemo.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(2),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "A1", BienSo = "51A-12345" } }
        });

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svcNoDemo.SimulateDemoPaymentAsync(booking.MaDon, 3, "Customer"));

        // DemoMode = true nhưng Customer khác gọi -> 404
        var policyDemo = new ParkingPolicySettings { DemoMode = true, WebhookSecret = WebhookSecret };
        var svcDemo = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policyDemo), NullLogger<ParkingOrderService>.Instance);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svcDemo.SimulateDemoPaymentAsync(booking.MaDon, 4, "Customer"));

        // Customer chính chủ -> Thành công
        var res = await svcDemo.SimulateDemoPaymentAsync(booking.MaDon, 3, "Customer");
        Assert.Equal("PAID", res.TrangThai);
    }

    [Fact]
    public async Task Test10_CashCodeBaoMat_LuuHmacSha256_KhoaSau5LanNhapSai_DungMotLan()
    {
        var db = CreateInMemoryDb(nameof(Test10_CashCodeBaoMat_LuuHmacSha256_KhoaSau5LanNhapSai_DungMotLan));
        var baseTime = new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { CashCodeMaxFailedAttempts = 5, CashCodeExpiryMin = 30, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E6", BienSo = "51K-55555" } }
        });

        // 1. Staff sinh mã (Base32/Crockford 6 ký tự, không dùng tiền tố CASH-)
        string plainCashCode = await service.StaffCreateCashCodeAsync(booking.MaDon, 2);
        Assert.Equal(6, plainCashCode.Length);
        Assert.Matches("^[0-9A-Z]{6}$", plainCashCode);

        // Kiểm tra trong DB: lưu băm HMAC-SHA256 (64 hex characters)
        var cashRecord = await db.MaTienMats.FindAsync(booking.MaDon);
        Assert.NotNull(cashRecord);
        Assert.Equal(64, cashRecord.MaCashCodeHash.Length);
        Assert.NotEqual(plainCashCode, cashRecord.MaCashCodeHash);

        // 2. Nhập sai 4 lần đầu
        for (int i = 1; i <= 4; i++)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.RedeemCashCodeAsync(new RedeemCashCodeRequest { MaDon = booking.MaDon, MaCashCode = "CASH-WRONG1" }, 3, "Customer"));
            Assert.Contains("còn", ex.Message);
        }

        // Lần thứ 5 sai -> Mã bị khóa
        var exLock = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RedeemCashCodeAsync(new RedeemCashCodeRequest { MaDon = booking.MaDon, MaCashCode = "CASH-WRONG1" }, 3, "Customer"));
        Assert.Contains("đã bị khóa", exLock.Message);

        var locked = await db.MaTienMats.FindAsync(booking.MaDon);
        Assert.True(locked!.BiKhoa);

        // Lần thứ 6 dù nhập đúng cũng bị từ chối
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RedeemCashCodeAsync(new RedeemCashCodeRequest { MaDon = booking.MaDon, MaCashCode = plainCashCode }, 3, "Customer"));
    }

    [Fact]
    public async Task Test11_HuyDonDatCho_GiaiPhongOHeld_VaKiemSoatQuyen()
    {
        var db = CreateInMemoryDb(nameof(Test11_HuyDonDatCho_GiaiPhongOHeld_VaKiemSoatQuyen));
        var baseTime = new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings { WebhookSecret = WebhookSecret }), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddMinutes(5),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "A2", BienSo = "51A-22222" } }
        });

        // Customer 4 cố hủy đơn của Customer 3 -> 404
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CancelOrderAsync(booking.MaDon, 4, "Customer"));

        // Customer 3 hủy đơn của mình -> Thành công, ô giải phóng về FREE
        var cancelled = await service.CancelOrderAsync(booking.MaDon, 3, "Customer");
        Assert.Equal("CANCELLED", cancelled.TrangThai);

        var slotAfter = await db.ViTriDoXes.FindAsync("A2");
        Assert.Equal("FREE", slotAfter!.TrangThai);
    }

    [Fact]
    public async Task Test12_BaoMatPhanQuyenVaOwnership_GetByIdTra404()
    {
        var db = CreateInMemoryDb(nameof(Test12_BaoMatPhanQuyenVaOwnership_GetByIdTra404));
        var baseTime = new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings { WebhookSecret = WebhookSecret }), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(2),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "A3", BienSo = "51A-33333" } }
        });

        // Customer 3 xem đơn của mình -> 200
        Assert.NotNull(await service.GetBookingByIdAsync(booking.MaDon, 3, "Customer"));

        // Customer 4 xem đơn của Customer 3 -> Trả null (Controller trả 404 BOLA)
        Assert.Null(await service.GetBookingByIdAsync(booking.MaDon, 4, "Customer"));

        // Staff xem đơn của Customer 3 -> 200
        Assert.NotNull(await service.GetBookingByIdAsync(booking.MaDon, 2, "Staff"));
    }

    [Fact]
    public async Task Test13_ThanhToanVaWorkerChayDongThoi_ChiDuyNhatMotTrangThaiCuoiCung_KhongMatTien()
    {
        string dbName = nameof(Test13_ThanhToanVaWorkerChayDongThoi_ChiDuyNhatMotTrangThaiCuoiCung_KhongMatTien);
        var db = CreateInMemoryDb(dbName);
        var baseTime = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { HoldMin = 5, DemoMode = true, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var booking = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddMinutes(5),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "B1", BienSo = "51A-12345" } }
        });

        // Tua thời gian sát lúc hết hạn 5 phút
        timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        // Hai luồng chạy đồng thời: một luồng khách thanh toán, một luồng worker quét hết hạn
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new ParkingTimeoutWorker(scopeFactory, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingTimeoutWorker>.Instance);

        var dbPay = CreateInMemoryDb(dbName);
        var payService = new ParkingOrderService(dbPay, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var taskPay = payService.SimulateDemoPaymentAsync(booking.MaDon, 3, "Customer");
        var taskWorker = worker.ProcessTimeoutsAsync();

        try
        {
            await Task.WhenAll(taskPay, taskWorker);
        }
        catch
        {
            // Dự kiến 1 trong 2 luồng có thể bị từ chối nếu luồng kia tới đích trước
        }

        using var verifyDb = CreateInMemoryDb(dbName);
        var finalOrder = await verifyDb.DonDatChos.Include(d => d.ThanhToans).FirstOrDefaultAsync(d => d.MaDon == booking.MaDon);

        // Khẳng định: Chỉ duy nhất 1 trạng thái cuối cùng (hoặc là PAID hoặc là EXPIRED)
        Assert.True(finalOrder!.TrangThai == "PAID" || finalOrder.TrangThai == "EXPIRED");

        // Không bao giờ có trường hợp: Đơn đã EXPIRED mà lại có bản ghi thanh toán PAID thành công (không mất tiền oan)
        if (finalOrder.TrangThai == "EXPIRED")
        {
            Assert.DoesNotContain(finalOrder.ThanhToans, t => t.TrangThai == "PAID");
        }
    }

    [Fact]
    public async Task Test14_DonANoShowNhungDonBGiuCungOKhungGioSau_OKhongBiNhaFreeSai()
    {
        string dbName = nameof(Test14_DonANoShowNhungDonBGiuCungOKhungGioSau_OKhongBiNhaFreeSai);
        var db = CreateInMemoryDb(dbName);
        var baseTime = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc); // 09:00 UTC+7
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { CheckInGraceMin = 15, DemoMode = true, WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        // Đơn A: 10:00 -> 11:00 UTC+7 ô E5 (Hạn check-in: 10:15)
        var orderA = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E5", BienSo = "51A-11111" } }
        });
        await service.SimulateDemoPaymentAsync(orderA.MaDon, 3, "Customer");

        // Đơn B: 14:00 -> 16:00 UTC+7 cùng ô E5 (đã thanh toán PAID, tương lai)
        var orderB = await service.CreateBookingAsync(4, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(5),
            GoiThoiLuongGio = 2,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E5", BienSo = "51B-22222" } }
        });
        await service.SimulateDemoPaymentAsync(orderB.MaDon, 4, "Customer");

        // Tua thời gian đến 10:16 (Đơn A quá hạn CheckInGraceMin 15 phút -> NO_SHOW)
        timeProvider.SetUtcNow(baseTime.AddHours(1).AddMinutes(16));

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new ParkingTimeoutWorker(scopeFactory, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingTimeoutWorker>.Instance);
        await worker.ProcessTimeoutsAsync();

        using var verifyDb = CreateInMemoryDb(dbName);
        var finalOrderA = await verifyDb.DonDatChos.FindAsync(orderA.MaDon);
        Assert.Equal("NO_SHOW", finalOrderA!.TrangThai);

        // Ô E5 KHÔNG được trở thành "FREE" sai, mà phải phản ánh trạng thái "BOOKED_FUTURE" của đơn B!
        var slotE5 = await verifyDb.ViTriDoXes.FindAsync("E5");
        Assert.Equal("BOOKED_FUTURE", slotE5!.TrangThai);
    }

    [Fact]
    public async Task Test15_CashCode_HaiDonKhacNhauCungMaVanDuocPhep_MaCuaDonKhacBiTuChoi()
    {
        var db = CreateInMemoryDb(nameof(Test15_CashCode_HaiDonKhacNhauCungMaVanDuocPhep_MaCuaDonKhacBiTuChoi));
        var baseTime = new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new ParkingPolicySettings { WebhookSecret = WebhookSecret };
        var service = new ParkingOrderService(db, timeProvider, new FakeOptionsMonitor<ParkingPolicySettings>(policy), NullLogger<ParkingOrderService>.Instance);

        var booking1 = await service.CreateBookingAsync(3, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "B2", BienSo = "51A-11111" } }
        });

        var booking2 = await service.CreateBookingAsync(4, new CreateBookingRequest
        {
            ThoiGianHenT = baseTime.AddHours(1),
            GoiThoiLuongGio = 1,
            DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "B3", BienSo = "51B-22222" } }
        });

        // Staff sinh CashCode cho đơn 1
        string code1 = await service.StaffCreateCashCodeAsync(booking1.MaDon, 2);

        // Staff sinh CashCode cho đơn 2
        string code2 = await service.StaffCreateCashCodeAsync(booking2.MaDon, 2);

        // Khách 2 cố lấy code1 áp dụng cho đơn 2 -> Bị từ chối
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RedeemCashCodeAsync(new RedeemCashCodeRequest { MaDon = booking2.MaDon, MaCashCode = code1 }, 4, "Customer"));

        // Khách 2 cố quy đổi đơn 1 -> 404 (BOLA)
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.RedeemCashCodeAsync(new RedeemCashCodeRequest { MaDon = booking1.MaDon, MaCashCode = code1 }, 4, "Customer"));

        // Hai đơn khác nhau lưu trong DB có PK là MaDon riêng biệt
        var record1 = await db.MaTienMats.FindAsync(booking1.MaDon);
        var record2 = await db.MaTienMats.FindAsync(booking2.MaDon);
        Assert.NotNull(record1);
        Assert.NotNull(record2);
        Assert.NotEqual(record1.MaDon, record2.MaDon);
    }

    [Fact]
    public async Task Test16_TichHopSqlServerThat_HaiDbContextSongSong_Lap20Lan_KhongDatTrung()
    {
        // Kiểm tra xem SQL Server cục bộ có kết nối được không (sử dụng database test riêng QL_BAIGIUXE_IOT_TEST)
        const string connStr = "Server=.\\SQLEXPRESS;Database=QL_BAIGIUXE_IOT_TEST;Trusted_Connection=True;TrustServerCertificate=True;";
        bool isSqlServerAvailable = false;
        try
        {
            using var testConn = new SqlConnection(connStr);
            await testConn.OpenAsync();
            isSqlServerAvailable = true;
        }
        catch
        {
            isSqlServerAvailable = false;
        }

        Assert.True(isSqlServerAvailable, "Cơ sở dữ liệu SQL Server thật QL_BAIGIUXE_IOT_TEST không thể kết nối!");

        var baseTime = new DateTime(2026, 12, 1, 8, 0, 0, DateTimeKind.Utc);

        // Dọn dẹp dữ liệu kiểm thử cũ nếu có từ lần chạy trước
        using (var cleanupDb = new SmartParkingDbContext(new DbContextOptionsBuilder<SmartParkingDbContext>().UseSqlServer(connStr).Options))
        {
            var oldOrders = await cleanupDb.DonDatChos
                .Where(d => d.ThoiGianHenT >= baseTime && d.ThoiGianHenT <= baseTime.AddDays(30))
                .ToListAsync();
            if (oldOrders.Any())
            {
                var orderIds = oldOrders.Select(o => o.MaDon).ToList();
                var oldDetails = await cleanupDb.DonDatChoViTris.Where(v => orderIds.Contains(v.MaDon)).ToListAsync();
                var oldPayments = await cleanupDb.ThanhToans.Where(t => orderIds.Contains(t.MaDon)).ToListAsync();
                var oldCodes = await cleanupDb.MaVaoCongs.Where(m => orderIds.Contains(m.MaDon)).ToListAsync();
                var oldCashCodes = await cleanupDb.MaTienMats.Where(c => orderIds.Contains(c.MaDon)).ToListAsync();
                var oldLogs = await cleanupDb.NhatKyHeThongs.Where(l => l.MaDon.HasValue && orderIds.Contains(l.MaDon.Value)).ToListAsync();
                cleanupDb.DonDatChoViTris.RemoveRange(oldDetails);
                cleanupDb.ThanhToans.RemoveRange(oldPayments);
                cleanupDb.MaVaoCongs.RemoveRange(oldCodes);
                cleanupDb.MaTienMats.RemoveRange(oldCashCodes);
                cleanupDb.NhatKyHeThongs.RemoveRange(oldLogs);
                cleanupDb.DonDatChos.RemoveRange(oldOrders);
                await cleanupDb.SaveChangesAsync();
            }
        }

        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings { WebhookSecret = WebhookSecret });

        // Lặp kiểm thử đúng 20 lần độc lập
        for (int iteration = 1; iteration <= 20; iteration++)
        {
            // Mỗi vòng lặp thử nghiệm đặt cùng một ô E7 tại khung giờ cách biệt
            DateTime henT = baseTime.AddDays(iteration);

            var reqA = new CreateBookingRequest
            {
                ThoiGianHenT = henT,
                GoiThoiLuongGio = 1,
                LoaiKhach = "DAT_TRUOC",
                DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E7", BienSo = $"51A-{iteration:D5}" } }
            };

            var reqB = new CreateBookingRequest
            {
                ThoiGianHenT = henT,
                GoiThoiLuongGio = 1,
                LoaiKhach = "DAT_TRUOC",
                DanhSachViTri = new List<BookingSlotItemDto> { new() { MaViTri = "E7", BienSo = $"51B-{iteration:D5}" } }
            };

            var optA = new DbContextOptionsBuilder<SmartParkingDbContext>().UseSqlServer(connStr).Options;
            var optB = new DbContextOptionsBuilder<SmartParkingDbContext>().UseSqlServer(connStr).Options;

            using var dbA = new SmartParkingDbContext(optA);
            using var dbB = new SmartParkingDbContext(optB);

            var svcA = new ParkingOrderService(dbA, timeProvider, policy, NullLogger<ParkingOrderService>.Instance);
            var svcB = new ParkingOrderService(dbB, timeProvider, policy, NullLogger<ParkingOrderService>.Instance);

            var taskA = Task.Run(() => svcA.CreateBookingAsync(3, reqA));
            var taskB = Task.Run(() => svcB.CreateBookingAsync(4, reqB));

            try
            {
                await Task.WhenAll(taskA, taskB);
            }
            catch
            {
                // Dự kiến 1 task thành công, 1 task xung đột (409)
            }

            int success = 0;
            int conflict = 0;

            if (taskA.IsCompletedSuccessfully) success++; else conflict++;
            if (taskB.IsCompletedSuccessfully) success++; else conflict++;

            // Khẳng định qua 20 lần chạy: ĐÚNG 1 thành công và ĐÚNG 1 thất bại xung đột
            Assert.Equal(1, success);
            Assert.Equal(1, conflict);
        }
    }
}
