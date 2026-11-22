using System.Text.Json;
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

public class FakeMqttPublisher : IMqttPublisher
{
    public List<(string Topic, string Payload, bool Retain)> PublishedMessages { get; } = new();
    public bool IsConnected => true;

    public Task PublishAsync(string topic, string payload, bool retain = false)
    {
        PublishedMessages.Add((topic, payload, retain));
        return Task.CompletedTask;
    }
}

public class ParkingPhase2bTests
{
    private static SmartParkingDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<SmartParkingDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var db = new SmartParkingDbContext(options);

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
                        TrangThai = "FREE",
                        KhoangCachCm = 150m,
                        LanCapNhat = DateTime.UtcNow
                    });
                }
            }

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

    [Fact]
    public async Task Test17_CheckInHaiBuoc_QuetDung_MoCong_QuetSaiVuotQuaLimit_BiKhoa()
    {
        string dbName = nameof(Test17_CheckInHaiBuoc_QuetDung_MoCong_QuetSaiVuotQuaLimit_BiKhoa);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            ScanRetryLimit = 3,
            GateTransitSec = 60,
            ParkingTimeoutMin = 10
        });

        // Tạo đơn PAID với biển số 51A-12345
        var don = new DonDatCho
        {
            MaDon = 101,
            MaTaiKhoan = 3,
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            HanCheckIn = baseTime.AddMinutes(15),
            TrangThai = "PAID",
            TongTien = 20000m
        };
        db.DonDatChos.Add(don);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 101, MaDon = 101, MaViTri = "E1", BienSo = "51A-12345", TrangThai = "BOOKED" });
        db.MaVaoCongs.Add(new MaVaoCong { MaEntryCode = "ENTRY101", MaDon = 101, BienSo = "51A-12345", TrangThai = "ACTIVE", HanSuDung = baseTime.AddMinutes(15), SoLanQuet = 0 });
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // 1. Quét sai biển số lần 1
        var ex1 = await Assert.ThrowsAsync<ArgumentException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "ENTRY101", BienSo = "51A-99999" }));
        Assert.Contains("không khớp", ex1.Message);

        // 2. Quét sai biển số lần 2
        var ex2 = await Assert.ThrowsAsync<ArgumentException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "ENTRY101", BienSo = "51A-99999" }));
        Assert.Contains("không khớp", ex2.Message);

        // 3. Quét sai biển số lần 3 -> Vượt quá ScanRetryLimit -> Khóa mã
        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "ENTRY101", BienSo = "51A-99999" }));
        Assert.Contains("tạm khóa", ex3.Message);

        // Tạo đơn PAID hợp lệ thứ hai để kiểm tra quét đúng và PASS
        var don2 = new DonDatCho
        {
            MaDon = 102,
            MaTaiKhoan = 3,
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            HanCheckIn = baseTime.AddMinutes(15),
            TrangThai = "PAID",
            TongTien = 20000m
        };
        db.DonDatChos.Add(don2);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 102, MaDon = 102, MaViTri = "E2", BienSo = "51B-88888", TrangThai = "BOOKED" });
        db.MaVaoCongs.Add(new MaVaoCong { MaDon = 102, MaEntryCode = "ENTRY102", BienSo = "51B-88888", TrangThai = "ACTIVE", HanSuDung = baseTime.AddMinutes(15), SoLanQuet = 0 });
        await db.SaveChangesAsync();

        // Quét đúng biển số
        var scanRes = await gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "ENTRY102", BienSo = "51B-88888" });
        Assert.True(scanRes.Success);
        Assert.Equal(60, scanRes.GateTransitSec);
        Assert.Contains(fakeMqtt.PublishedMessages, m => m.Topic.Contains("gate/in/cmd") && m.Payload.Contains("OPEN"));

        // Bước 2: Thiết bị báo PASS qua cổng vào
        var passRes = await gateService.ProcessGateInPassAsync(102);
        Assert.Equal("ENTERED", passRes.TrangThai);
        Assert.NotNull(passRes.HanDoVaoO);

        var slotE2 = await db.ViTriDoXes.FindAsync("E2");
        Assert.Equal("GUIDING", slotE2!.TrangThai);
    }

    [Fact]
    public async Task Test18_XeRaKhiChuaDo_ChuyenSangAbandoned_NhaODo()
    {
        string dbName = nameof(Test18_XeRaKhiChuaDo_ChuyenSangAbandoned_NhaODo);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings());

        var don = new DonDatCho
        {
            MaDon = 201,
            MaTaiKhoan = 3,
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            TrangThai = "ENTERED",
            ThoiGianVaoCong = baseTime,
            HanDoVaoO = baseTime.AddMinutes(10),
            TongTien = 20000m
        };
        db.DonDatChos.Add(don);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 201, MaDon = 201, MaViTri = "E3", BienSo = "51C-77777", TrangThai = "GUIDING" });
        var slotE3 = await db.ViTriDoXes.FindAsync("E3");
        slotE3!.TrangThai = "GUIDING";
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // Xe đi thẳng ra cổng mà chưa đỗ vào ô
        var outRes = await gateService.ScanGateOutAsync(new GateOutScanRequest { OrderId = 201 });
        Assert.True(outRes.Success);
        Assert.Equal("ABANDONED", outRes.Status);

        // Khẳng định đơn chuyển ABANDONED và ô đỗ được nhả về FREE
        var updatedDon = await db.DonDatChos.FindAsync(201L);
        Assert.Equal("ABANDONED", updatedDon!.TrangThai);
        Assert.NotNull(updatedDon.ThoiGianRaCong);

        var updatedSlot = await db.ViTriDoXes.FindAsync("E3");
        Assert.Equal("FREE", updatedSlot!.TrangThai);
        Assert.Contains(fakeMqtt.PublishedMessages, m => m.Topic.Contains("gate/out/cmd") && m.Payload.Contains("OPEN"));
    }

    [Fact]
    public async Task Test19_BaiDongXeDangTimCho_MaxInLotSearching_TuChoiMoCong()
    {
        string dbName = nameof(Test19_BaiDongXeDangTimCho_MaxInLotSearching_TuChoiMoCong);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            MaxInLotSearching = 3
        });

        // Tạo 3 xe đang ở trạng thái ENTERED (đang tìm chỗ)
        for (int i = 1; i <= 3; i++)
        {
            db.DonDatChos.Add(new DonDatCho
            {
                MaDon = 300 + i,
                MaTaiKhoan = 3,
                ThoiGianDat = baseTime,
                ThoiGianHenT = baseTime,
                ThoiGianKetThucDuKien = baseTime.AddHours(2),
                TrangThai = "ENTERED",
                ThoiGianVaoCong = baseTime,
                HanDoVaoO = baseTime.AddMinutes(10),
                TongTien = 20000m
            });
        }

        // Đơn thứ 4 chuẩn bị quét vào
        db.DonDatChos.Add(new DonDatCho
        {
            MaDon = 304,
            MaTaiKhoan = 4,
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            HanCheckIn = baseTime.AddMinutes(15),
            TrangThai = "PAID",
            TongTien = 20000m
        });
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 304, MaDon = 304, MaViTri = "E4", BienSo = "51D-44444", TrangThai = "BOOKED" });
        db.MaVaoCongs.Add(new MaVaoCong { MaEntryCode = "ENTRY304", MaDon = 304, BienSo = "51D-44444", TrangThai = "ACTIVE", HanSuDung = baseTime.AddMinutes(15) });
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // Quét cổng vào -> Bị từ chối vì bãi đông
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "ENTRY304", BienSo = "51D-44444" }));

        Assert.Contains("đông xe đang tìm ô đỗ", ex.Message);
        Assert.Contains(fakeMqtt.PublishedMessages, m => m.Topic.Contains("gate/in/cmd") && m.Payload.Contains("HOLD"));
    }

    [Fact]
    public async Task Test20_NutToiDangKet_VaCheckpoint_GiaHanThoiGianVaoO()
    {
        string dbName = nameof(Test20_NutToiDangKet_VaCheckpoint_GiaHanThoiGianVaoO);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            ExtensionMin = 5,
            MaxExtensions = 1
        });

        var don = new DonDatCho
        {
            MaDon = 401,
            MaTaiKhoan = 3,
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            TrangThai = "ENTERED",
            ThoiGianVaoCong = baseTime,
            HanDoVaoO = baseTime.AddMinutes(10),
            SoLanGiaHan = 0,
            TongTien = 20000m
        };
        db.DonDatChos.Add(don);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 401, MaDon = 401, MaViTri = "E5", BienSo = "51E-55555", TrangThai = "GUIDING" });
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // Khách bấm "Tôi đang kẹt" lần 1 -> Hạn vào ô tăng thêm 5 phút
        var res1 = await gateService.ReportTrafficJamAsync(401, 3);
        Assert.Equal(baseTime.AddMinutes(15), res1.HanDoVaoO);
        Assert.Equal(1, res1.SoLanGiaHan);

        // Khách bấm "Tôi đang kẹt" lần 2 -> Ném lỗi vì vượt MaxExtensions = 1
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateService.ReportTrafficJamAsync(401, 3));
        Assert.Contains("hết số lượt gia hạn", ex.Message);

        // Xe qua Checkpoint trạm kiểm soát -> Tự động gia hạn thêm ExtensionMin
        await gateService.PassCheckpointAsync("CP1", 401);
        var updatedDon = await db.DonDatChos.FindAsync(401L);
        Assert.Equal(baseTime.AddMinutes(20), updatedDon!.HanDoVaoO);
    }

    [Fact]
    public async Task Test21_HetHanVaoOMaChuaDo_TuDoiOSangFree_NeuHetChoThiFreeExit()
    {
        string dbName = nameof(Test21_HetHanVaoOMaChuaDo_TuDoiOSangFree_NeuHetChoThiFreeExit);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            ParkingTimeoutMin = 10,
            NoSlotPolicy = "FreeExit"
        });

        // 1. Trường hợp còn ô FREE: Đổi sang ô FREE gần nhất
        var don1 = new DonDatCho
        {
            MaDon = 501,
            MaTaiKhoan = 3,
            ThoiGianDat = baseTime.AddMinutes(-30),
            ThoiGianHenT = baseTime.AddMinutes(-30),
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            TrangThai = "ENTERED",
            HanDoVaoO = baseTime.AddMinutes(-1), // Đã hết hạn vào ô
            TongTien = 20000m
        };
        db.DonDatChos.Add(don1);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 501, MaDon = 501, MaViTri = "E1", BienSo = "51F-11111", TrangThai = "GUIDING" });
        var slotE1 = await db.ViTriDoXes.FindAsync("E1");
        slotE1!.TrangThai = "GUIDING";
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        services.AddSingleton<IMqttPublisher>(fakeMqtt);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new ParkingTimeoutWorker(scopeFactory, timeProvider, policy, NullLogger<ParkingTimeoutWorker>.Instance);
        await worker.ProcessTimeoutsAsync();

        // Ô mới được chỉ định (ô FREE đầu tiên là A1 hoặc E2 tùy sắp xếp)
        using (var verifyDb1 = CreateInMemoryDb(dbName))
        {
            var updatedDon1 = await verifyDb1.DonDatChos.Include(d => d.DonDatChoViTris).FirstAsync(d => d.MaDon == 501);
            string newSlotId = updatedDon1.DonDatChoViTris.First().MaViTri;
            Assert.NotEqual("E1", newSlotId);
            Assert.Equal("ENTERED", updatedDon1.TrangThai);
            Assert.True(updatedDon1.HanDoVaoO > baseTime); // Hạn mới được gia hạn thêm
        }

        // 2. Trường hợp bãi hết chỗ: Kích hoạt NoSlotPolicy = FreeExit
        // Giả lập tất cả các ô trong bãi đều OCCUPIED
        using (var setupDb = CreateInMemoryDb(dbName))
        {
            var allSlots = await setupDb.ViTriDoXes.ToListAsync();
            foreach (var s in allSlots)
            {
                s.TrangThai = "OCCUPIED";
            }

            var don2 = new DonDatCho
            {
                MaDon = 502,
                MaTaiKhoan = 4,
                ThoiGianDat = baseTime.AddMinutes(-30),
                ThoiGianHenT = baseTime.AddMinutes(-30),
                ThoiGianKetThucDuKien = baseTime.AddHours(2),
                TrangThai = "ENTERED",
                HanDoVaoO = baseTime.AddMinutes(-1),
                TongTien = 20000m
            };
            setupDb.DonDatChos.Add(don2);
            setupDb.DonDatChoViTris.Add(new DonDatChoViTri { MaChiTiet = 502, MaDon = 502, MaViTri = "E8", BienSo = "51G-22222", TrangThai = "GUIDING" });
            await setupDb.SaveChangesAsync();
        }

        await worker.ProcessTimeoutsAsync();

        using (var verifyDb2 = CreateInMemoryDb(dbName))
        {
            var updatedDon2 = await verifyDb2.DonDatChos.FindAsync(502L);
            Assert.Equal("COMPLETED", updatedDon2!.TrangThai);
            Assert.Contains("FreeExit", updatedDon2.GhiChu);
            Assert.Contains(fakeMqtt.PublishedMessages, m => m.Topic.Contains("gate/out/cmd") && m.Payload.Contains("OPEN"));
        }
    }

    [Fact]
    public async Task Test22_OverlapBangKhoangCach_PartitalVaAdjacent_PhatHienLanVach()
    {
        string dbName = nameof(Test22_OverlapBangKhoangCach_PartitalVaAdjacent_PhatHienLanVach);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            OccupiedMaxCm = 40.0,
            PartialMaxCm = 80.0,
            OverlapWindowSec = 30
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        services.AddScoped<GateService>();
        services.AddSingleton<IMqttPublisher>(fakeMqtt);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<IOptionsMonitor<ParkingPolicySettings>>(policy);
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // Khởi tạo MqttBackgroundService
        var mqttOptions = Options.Create(new MqttSettings { TopicPrefix = "dacn-smartparking/lot1" });
        var mqttService = new MqttBackgroundService(NullLogger<MqttBackgroundService>.Instance, scopeFactory, mqttOptions);

        // Mô phỏng nhận message slot/E1/state với distanceCm = 55.0 (PARTIAL)
        var handleMethod = typeof(MqttBackgroundService).GetMethod("HandleSlotStateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(handleMethod);

        using (var scope = scopeFactory.CreateScope())
        {
            var task1 = (Task)handleMethod.Invoke(mqttService, new object[] { scope, "slot/E1/state", "{\"distanceCm\": 55.0}" })!;
            await task1;
        }

        using (var verifyDb = CreateInMemoryDb(dbName))
        {
            var slotE1 = await verifyDb.ViTriDoXes.FindAsync("E1");
            Assert.Equal("PARTIAL", slotE1!.TrangThai);
        }

        // Mô phỏng nhận message slot/E2/state (ô liền kề) với distanceCm = 65.0 trong vòng 30s
        using (var scope = scopeFactory.CreateScope())
        {
            var task2 = (Task)handleMethod.Invoke(mqttService, new object[] { scope, "slot/E2/state", "{\"distanceCm\": 65.0}" })!;
            await task2;
        }

        using (var verifyDb = CreateInMemoryDb(dbName))
        {
            var slotE2 = await verifyDb.ViTriDoXes.FindAsync("E2");
            Assert.Equal("OVERLAP", slotE2!.TrangThai);

            // Khẳng định có cảnh báo OVERLAP được ghi vào nhật ký hệ thống
            var warningLog = await verifyDb.NhatKyHeThongs.FirstOrDefaultAsync(l => l.LoaiSuKien == "OVERLAP_WARNING");
            Assert.NotNull(warningLog);
            Assert.Contains("OVERLAP", warningLog.NoiDung);
        }
    }

    [Fact]
    public async Task Test23_MatDienDutMangLWT_DanhDauUnknown_VaAuditXeDoChui()
    {
        string dbName = nameof(Test23_MatDienDutMangLWT_DanhDauUnknown_VaAuditXeDoChui);
        using var db = CreateInMemoryDb(dbName);

        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var fakeMqtt = new FakeMqttPublisher();
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings());

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        services.AddScoped<GateService>();
        services.AddSingleton<IMqttPublisher>(fakeMqtt);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<IOptionsMonitor<ParkingPolicySettings>>(policy);
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var mqttOptions = Options.Create(new MqttSettings { TopicPrefix = "dacn-smartparking/lot1" });
        var mqttService = new MqttBackgroundService(NullLogger<MqttBackgroundService>.Instance, scopeFactory, mqttOptions);

        var handleDeviceMethod = typeof(MqttBackgroundService).GetMethod("HandleDeviceStatusAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handleResyncMethod = typeof(MqttBackgroundService).GetMethod("HandleResyncAuditAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(handleDeviceMethod);
        Assert.NotNull(handleResyncMethod);

        // 1. MQTT LWT: Thiết bị esp32 gửi OFFLINE
        using (var scope = scopeFactory.CreateScope())
        {
            var taskOffline = (Task)handleDeviceMethod.Invoke(mqttService, new object[] { scope, "device/esp32_lot1/status", "OFFLINE" })!;
            await taskOffline;
        }

        using (var verifyDb = CreateInMemoryDb(dbName))
        {
            // Toàn bộ các ô vật lý hàng E phải chuyển sang UNKNOWN
            var rowESlots = await verifyDb.ViTriDoXes.Where(s => s.Hang == "E").ToListAsync();
            Assert.All(rowESlots, s => Assert.Equal("UNKNOWN", s.TrangThai));

            var offlineLog = await verifyDb.NhatKyHeThongs.FirstOrDefaultAsync(l => l.LoaiSuKien == "DEVICE_OFFLINE_LWT");
            Assert.NotNull(offlineLog);

            // 2. Thiết bị kết nối lại (ONLINE / Resync Audit)
            // Đặt ô E5 có vật cản (khoảng cách 30cm - OCCUPIED) nhưng KHÔNG có đơn nào trong DB
            var slotE5 = await verifyDb.ViTriDoXes.FindAsync("E5");
            slotE5!.KhoangCachCm = 30m;
            slotE5.TrangThai = "OCCUPIED";
            await verifyDb.SaveChangesAsync();
        }

        using (var scope = scopeFactory.CreateScope())
        {
            var taskAudit = (Task)handleResyncMethod.Invoke(mqttService, new object[] { scope, "resync" })!;
            await taskAudit;
        }

        using (var verifyDb = CreateInMemoryDb(dbName))
        {
            // Hệ thống phải phát hiện và ghi log UNAUTHORIZED_PARKING (xe đỗ chui)
            var unauthorizedLog = await verifyDb.NhatKyHeThongs.FirstOrDefaultAsync(l => l.LoaiSuKien == "UNAUTHORIZED_PARKING");
            Assert.NotNull(unauthorizedLog);
            Assert.Contains("E5", unauthorizedLog.NoiDung);
        }
    }

    [Fact]
    public async Task Test24_Checkpoint_LapLaiCungCheckpoint_KhongGiaHanThem()
    {
        string dbName = nameof(Test24_Checkpoint_LapLaiCungCheckpoint_KhongGiaHanThem);
        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            ParkingTimeoutMin = 10,
            ExtensionMin = 5,
            MaxTotalExtensionMin = 15
        });
        var fakeMqtt = new FakeMqttPublisher();

        using var db = CreateInMemoryDb(dbName);
        var don = new DonDatCho
        {
            MaDon = 2401,
            MaTaiKhoan = 2,
            LoaiKhach = "DAT_TRUOC",
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            GoiThoiLuongGio = 2,
            TongTien = 20000m,
            TrangThai = "ENTERED",
            ThoiGianVaoCong = baseTime,
            HanDoVaoO = baseTime.AddMinutes(10)
        };
        db.DonDatChos.Add(don);
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // 1. Xe qua trạm kiểm soát CP1 lần đầu -> Gia hạn thành công thêm 5 phút (từ 10m lên 15m)
        bool ext1 = await gateService.PassCheckpointAsync("CP1", 2401);
        Assert.True(ext1);

        var donAfterExt1 = await db.DonDatChos.FindAsync(2401L);
        Assert.Equal(baseTime.AddMinutes(15), donAfterExt1!.HanDoVaoO);
        Assert.Contains("[CP:CP1]", donAfterExt1.GhiChu);

        // 2. Xe quét lại cùng trạm kiểm soát CP1 -> KHÔNG gia hạn thêm (giữ nguyên 15 phút)
        bool ext2 = await gateService.PassCheckpointAsync("CP1", 2401);
        Assert.False(ext2);

        var donAfterExt2 = await db.DonDatChos.FindAsync(2401L);
        Assert.Equal(baseTime.AddMinutes(15), donAfterExt2!.HanDoVaoO);

        // 3. Xe qua trạm kiểm soát khác CP2 -> Gia hạn thành công thêm 5 phút (lên 20m)
        bool ext3 = await gateService.PassCheckpointAsync("CP2", 2401);
        Assert.True(ext3);

        var donAfterExt3 = await db.DonDatChos.FindAsync(2401L);
        Assert.Equal(baseTime.AddMinutes(20), donAfterExt3!.HanDoVaoO);
        Assert.Contains("[CP:CP2]", donAfterExt3.GhiChu);
    }

    [Fact]
    public async Task Test25_BaiDong_Hold_KhiXeTimGiamXuong_CongTuMoLai_VaXeChoKhongBiTinhHanDo()
    {
        string dbName = nameof(Test25_BaiDong_Hold_KhiXeTimGiamXuong_CongTuMoLai_VaXeChoKhongBiTinhHanDo);
        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            MaxInLotSearching = 3,
            CheckInGraceMin = 15,
            ParkingTimeoutMin = 10
        });
        var fakeMqtt = new FakeMqttPublisher();

        using var db = CreateInMemoryDb(dbName);

        // Tạo 3 đơn đang ENTERED (đang tìm ô) -> Bãi đã đạt ngưỡng MaxInLotSearching = 3
        for (int i = 1; i <= 3; i++)
        {
            db.DonDatChos.Add(new DonDatCho
            {
                MaDon = 2500 + i,
                MaTaiKhoan = 2,
                LoaiKhach = "DAT_TRUOC",
                ThoiGianDat = baseTime,
                ThoiGianHenT = baseTime,
                ThoiGianKetThucDuKien = baseTime.AddHours(2),
                GoiThoiLuongGio = 2,
                TongTien = 20000m,
                TrangThai = "ENTERED",
                ThoiGianVaoCong = baseTime,
                HanDoVaoO = baseTime.AddMinutes(10)
            });
        }

        // Đơn thứ 4 chuẩn bị vào cổng
        var donWaiting = new DonDatCho
        {
            MaDon = 2504,
            MaTaiKhoan = 2,
            LoaiKhach = "DAT_TRUOC",
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            GoiThoiLuongGio = 2,
            TongTien = 20000m,
            TrangThai = "PAID",
            HanDoVaoO = null // Xe chưa qua barie -> Chưa tính đồng hồ vào ô
        };
        db.DonDatChos.Add(donWaiting);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaDon = 2504, MaViTri = "E4", BienSo = "51F-25004" });
        db.MaVaoCongs.Add(new MaVaoCong
        {
            MaEntryCode = "CODE2504",
            MaDon = 2504,
            BienSo = "51F-25004",
            HanSuDung = baseTime.AddMinutes(15),
            TrangThai = "ACTIVE"
        });
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // 1. Quét xe 4 -> Bị từ chối vì bãi đông (HOLD)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "CODE2504", BienSo = "51F-25004" }));
        Assert.Contains("đông xe", ex.Message);

        // Xe đang chờ trước cổng KHÔNG bị tính đồng hồ HanDoVaoO
        var reloadedWaiting = await db.DonDatChos.FindAsync(2504L);
        Assert.Null(reloadedWaiting!.HanDoVaoO);

        // 2. Một xe đang tìm chỗ đỗ thành công vào ô (chuyển sang PARKED qua ProcessSlotTelemetryAsync)
        // Ô E1 có xe đỗ vào cự ly 30cm
        var don1 = await db.DonDatChos.FindAsync(2501L);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaDon = 2501, MaViTri = "E1", BienSo = "51F-25001" });
        await db.SaveChangesAsync();

        await gateService.ProcessSlotTelemetryAsync("E1", 30.0, "SENSOR");

        // 3. Số xe tìm chỗ giảm xuống 2 (< 3) -> Cổng tự động phát lệnh UNHOLD/OPEN
        var unholdMsg = fakeMqtt.PublishedMessages.LastOrDefault(m => m.Topic == "dacn-smartparking/lot1/gate/in/cmd" && m.Payload.Contains("UNHOLD"));
        Assert.NotNull(unholdMsg.Payload);
        Assert.Contains("UNHOLD", unholdMsg.Payload);

        // 4. Xe thứ 4 quét lại -> Thành công mở cổng!
        var scanResult = await gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "CODE2504", BienSo = "51F-25004" });
        Assert.True(scanResult.Success);
    }

    [Fact]
    public async Task Test26_MaVaoCong_BiKhoaDoQuetSai_StaffMoKhoaThanhCong()
    {
        string dbName = nameof(Test26_MaVaoCong_BiKhoaDoQuetSai_StaffMoKhoaThanhCong);
        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            ScanRetryLimit = 3,
            CheckInGraceMin = 15
        });
        var fakeMqtt = new FakeMqttPublisher();

        using var db = CreateInMemoryDb(dbName);
        var don = new DonDatCho
        {
            MaDon = 2601,
            MaTaiKhoan = 2,
            LoaiKhach = "DAT_TRUOC",
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2),
            GoiThoiLuongGio = 2,
            TongTien = 20000m,
            TrangThai = "PAID"
        };
        db.DonDatChos.Add(don);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaDon = 2601, MaViTri = "E2", BienSo = "51F-99999" });
        db.MaVaoCongs.Add(new MaVaoCong
        {
            MaEntryCode = "LOCKCODE",
            MaDon = 2601,
            BienSo = "51F-99999",
            HanSuDung = baseTime.AddMinutes(15),
            TrangThai = "ACTIVE",
            SoLanQuet = 0
        });
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // 1. Quét sai 2 lần đầu -> Bị từ chối
        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "LOCKCODE", BienSo = "WRONG-1" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "LOCKCODE", BienSo = "WRONG-2" }));

        // 2. Quét sai lần 3 (đạt limit 3) -> Mã bị khóa (LOCKED)
        var lockEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "LOCKCODE", BienSo = "WRONG-3" }));
        Assert.Contains("tạm khóa", lockEx.Message);

        var lockedEntry = await db.MaVaoCongs.FindAsync("LOCKCODE");
        Assert.Equal("LOCKED", lockedEntry!.TrangThai);

        // 3. Quét đúng biển số nhưng mã đang LOCKED -> Vẫn bị từ chối
        var stillLockedEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "LOCKCODE", BienSo = "51F-99999" }));
        Assert.Contains("tạm khóa", stillLockedEx.Message);

        // 4. Staff gọi hàm mở khóa mã
        bool unlocked = await gateService.UnlockEntryCodeAsync("LOCKCODE", "staff01");
        Assert.True(unlocked);

        var unlockedEntry = await db.MaVaoCongs.FindAsync("LOCKCODE");
        Assert.Equal("ACTIVE", unlockedEntry!.TrangThai);
        Assert.Equal(0, unlockedEntry.SoLanQuet);

        // 5. Quét lại đúng biển số -> Thành công mở cổng barie!
        var successResult = await gateService.ScanGateInAsync(new GateInScanRequest { EntryCode = "LOCKCODE", BienSo = "51F-99999" });
        Assert.True(successResult.Success);
    }

    [Fact]
    public async Task Test27_XeQuaGioDat_Overstay_ChanCongRa_ThuPhatMoiMo()
    {
        string dbName = nameof(Test27_XeQuaGioDat_Overstay_ChanCongRa_ThuPhatMoiMo);
        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            PricePerHour = 10000,
            PenaltyPerMinute = 200,
            CheckInGraceMin = 15
        });
        var fakeMqtt = new FakeMqttPublisher();
        using var db = CreateInMemoryDb(dbName);

        // Tạo đơn PARKED từ 10:00 đến 12:00
        var don = new DonDatCho
        {
            MaDon = 2701,
            MaTaiKhoan = 3,
            LoaiKhach = "DAT_TRUOC",
            ThoiGianDat = baseTime,
            ThoiGianHenT = baseTime,
            ThoiGianKetThucDuKien = baseTime.AddHours(2), // 12:00
            GoiThoiLuongGio = 2,
            TongTien = 20000m,
            TrangThai = "PARKED"
        };
        db.DonDatChos.Add(don);
        db.DonDatChoViTris.Add(new DonDatChoViTri { MaDon = 2701, MaViTri = "E3", BienSo = "51F-27011", TrangThai = "OCCUPIED" });
        await db.SaveChangesAsync();

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // 1. Tua đồng hồ đến 12:30 (quá giờ 30 phút)
        timeProvider.Advance(TimeSpan.FromMinutes(150));

        // 2. Xe ra quét cổng -> Bị chặn do OVERSTAY (Status = OVERSTAY, RequireStaffPayment = true, phạt 30 * 200 = 6000đ)
        var scanOut = await gateService.ScanGateOutAsync(new GateOutScanRequest { OrderId = 2701 });
        Assert.False(scanOut.Success);
        Assert.Equal("OVERSTAY", scanOut.Status);
        Assert.True(scanOut.RequireStaffPayment);
        Assert.Equal(6000m, scanOut.PenaltyFee);
        Assert.Equal(30, scanOut.PenaltyMinutes);

        // 3. Nhân viên xác nhận thu phí phạt tại quầy -> Cổng mở, đơn chuyển COMPLETED
        var confirmResult = await gateService.ConfirmOverstayPaymentAsync(new OverstayConfirmRequest
        {
            OrderId = 2701,
            AmountPaid = 6000m,
            StaffNote = "Thu phí phạt quá giờ thành công"
        }, "staff01");

        Assert.Equal("COMPLETED", confirmResult.Status);
        var donAfter = await db.DonDatChos.FindAsync((long)2701);
        Assert.Equal("COMPLETED", donAfter!.TrangThai);
        var slotE3 = await db.ViTriDoXes.FindAsync("E3");
        Assert.Equal("FREE", slotE3!.TrangThai);
    }

    [Fact]
    public async Task Test28_BaoMatCong_TuChoiPassKhiChuaCoLenhMoCong()
    {
        string dbName = nameof(Test28_BaoMatCong_TuChoiPassKhiChuaCoLenhMoCong);
        var baseTime = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(baseTime);
        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings());
        var fakeMqtt = new FakeMqttPublisher();
        using var db = CreateInMemoryDb(dbName);

        var gateService = new GateService(db, timeProvider, policy, fakeMqtt, NullLogger<GateService>.Instance);

        // Gọi ProcessGateInPassAsync khi chưa hề có đơn nào quét hợp lệ mở cổng -> Phải ném ngoại lệ KeyNotFoundException
        await Assert.ThrowsAsync<KeyNotFoundException>(() => gateService.ProcessGateInPassAsync(null));
    }

    [Fact]
    public async Task Test29_MqttReconnect_VaResubscribeAllTopics()
    {
        string dbName = nameof(Test29_MqttReconnect_VaResubscribeAllTopics);
        using var db = CreateInMemoryDb(dbName);

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var mqttOptions = Options.Create(new MqttSettings { TopicPrefix = "dacn-smartparking/lot1" });
        var mqttService = new MqttBackgroundService(NullLogger<MqttBackgroundService>.Instance, scopeFactory, mqttOptions);

        Assert.NotNull(mqttService);
        var subMethod = typeof(MqttBackgroundService).GetMethod("SubscribeTopicsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(subMethod);

        // Gọi subscribe topics (khi client chưa connect thì gracefully return)
        var task = (Task)subMethod.Invoke(mqttService, null)!;
        await task;
    }

    [Fact]
    public async Task Test30_Overlap_KhoiPhucTuDatabase_SauKhiKhoiDongLai()
    {
        string dbName = nameof(Test30_Overlap_KhoiPhucTuDatabase_SauKhiKhoiDongLai);
        using var db = CreateInMemoryDb(dbName);
        var fakeMqtt = new FakeMqttPublisher();

        // Chuẩn bị 2 ô liền kề E1 (60cm) và E2 (65cm) trong database
        var slotE1 = await db.ViTriDoXes.FindAsync("E1");
        var slotE2 = await db.ViTriDoXes.FindAsync("E2");
        slotE1!.KhoangCachCm = 60m;
        slotE1.TrangThai = "FREE";
        slotE2!.KhoangCachCm = 65m;
        slotE2.TrangThai = "FREE";
        await db.SaveChangesAsync();

        // Xóa sạch RAM cự ly
        MqttBackgroundService.ClearRecentReadings();
        Assert.Equal(0, MqttBackgroundService.GetRecentReadingsCount());

        // Gọi hàm khôi phục cự ly và tính toán lại OVERLAP từ DB
        int overlapCount = await MqttBackgroundService.RestoreAndRecalculateOverlapFromDbAsync(db, fakeMqtt, null, "dacn-smartparking/lot1");

        // Khẳng định phát hiện được cặp ô OVERLAP
        Assert.True(overlapCount > 0);
        var verifyE1 = await db.ViTriDoXes.FindAsync("E1");
        var verifyE2 = await db.ViTriDoXes.FindAsync("E2");
        Assert.Equal("OVERLAP", verifyE1!.TrangThai);
        Assert.Equal("OVERLAP", verifyE2!.TrangThai);

        // Khẳng định RAM đã được nạp lại
        Assert.NotNull(MqttBackgroundService.GetRecentReading("E1"));
        Assert.NotNull(MqttBackgroundService.GetRecentReading("E2"));

        // Khẳng định có phát lệnh LED YELLOW không retain
        var yellowCmds = fakeMqtt.PublishedMessages.Where(m => m.Payload == "YELLOW" && !m.Retain).ToList();
        Assert.NotEmpty(yellowCmds);
    }

    [Fact]
    public async Task Test31_MatDienTungBoard_ChiAnhHuongODoThuocHangDo()
    {
        string dbName = nameof(Test31_MatDienTungBoard_ChiAnhHuongODoThuocHangDo);
        using var db = CreateInMemoryDb(dbName);
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var mqttOptions = Options.Create(new MqttSettings { TopicPrefix = "dacn-smartparking/lot1" });
        var mqttService = new MqttBackgroundService(NullLogger<MqttBackgroundService>.Instance, scopeFactory, mqttOptions);

        var handleMethod = typeof(MqttBackgroundService).GetMethod("HandleDeviceStatusAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(handleMethod);

        // Board hàng A bị OFFLINE
        using (var scope = scopeFactory.CreateScope())
        {
            var task = (Task)handleMethod.Invoke(mqttService, new object[] { scope, "device/esp32_row_a/status", "OFFLINE" })!;
            await task;
        }

        using (var verifyDb = CreateInMemoryDb(dbName))
        {
            // Hàng A chuyển UNKNOWN
            var rowASlots = await verifyDb.ViTriDoXes.Where(s => s.Hang == "A").ToListAsync();
            Assert.All(rowASlots, s => Assert.Equal("UNKNOWN", s.TrangThai));

            // Hàng E và các hàng khác vẫn giữ nguyên FREE
            var rowESlots = await verifyDb.ViTriDoXes.Where(s => s.Hang == "E").ToListAsync();
            Assert.All(rowESlots, s => Assert.Equal("FREE", s.TrangThai));
        }
    }

    [Fact]
    public async Task Test32_Chay12KichBanLienTiep3Lan_KhongReset_ThanhCong100PhanTram()
    {
        string dbName = nameof(Test32_Chay12KichBanLienTiep3Lan_KhongReset_ThanhCong100PhanTram);
        using var db = CreateInMemoryDb(dbName);

        var policy = new FakeOptionsMonitor<ParkingPolicySettings>(new ParkingPolicySettings
        {
            DemoMode = true,
            PricePerHour = 10000,
            PenaltyPerMinute = 200,
            CheckInGraceMin = 15,
            ParkingTimeoutMin = 10,
            HoldMin = 5,
            GateTransitSec = 60,
            ExtensionMin = 5,
            MaxExtensions = 1,
            ScanRetryLimit = 3,
            OccupiedMaxCm = 40.0,
            PartialMaxCm = 80.0,
            OverlapWindowSec = 30
        });
        var timeProvider = new ScaledTimeProvider(policy);

        var fakeMqtt = new FakeMqttPublisher();
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDb(dbName));
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton(timeProvider);
        services.AddSingleton<IOptionsMonitor<ParkingPolicySettings>>(policy);
        services.AddSingleton<IMqttPublisher>(fakeMqtt);
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<IParkingNotificationService, ParkingNotificationService>();
        services.AddScoped<ParkingOrderService>();
        services.AddScoped<GateService>();
        services.AddSingleton<ParkingTimeoutWorker>();

        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();

        var controller = new DemoController(
            scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>(),
            timeProvider,
            scope.ServiceProvider.GetRequiredService<ParkingTimeoutWorker>(),
            policy,
            scope.ServiceProvider.GetRequiredService<IParkingNotificationService>(),
            fakeMqtt,
            scope.ServiceProvider.GetRequiredService<GateService>(),
            scope.ServiceProvider.GetRequiredService<ParkingOrderService>(),
            NullLogger<DemoController>.Instance);

        // Chạy liên tiếp 3 vòng cho toàn bộ 12 kịch bản mà KHÔNG reset giữa chừng
        for (int round = 1; round <= 3; round++)
        {
            for (int scenarioId = 1; scenarioId <= 12; scenarioId++)
            {
                var result = await controller.ExecuteScenarioAsync(scenarioId);
                Assert.NotNull(result);

                var json = JsonSerializer.Serialize(result);
                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.GetProperty("passed").GetBoolean(),
                    $"Kịch bản {scenarioId} thất bại ở vòng {round}!");
            }
        }
    }
}
