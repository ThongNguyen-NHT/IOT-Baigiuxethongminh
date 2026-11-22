USE master;
GO

-- 1. Tao database QL_BAIGIUXE_IOT_TEST neu chua co
IF DB_ID(N'QL_BAIGIUXE_IOT_TEST') IS NULL
BEGIN
    CREATE DATABASE QL_BAIGIUXE_IOT_TEST;
    PRINT N'Da tao database QL_BAIGIUXE_IOT_TEST thanh cong.';
END
ELSE
BEGIN
    PRINT N'Database QL_BAIGIUXE_IOT_TEST da ton tai.';
END;
GO

USE QL_BAIGIUXE_IOT_TEST;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ============================================================
   BANG 1: TAI KHOAN NGUOI DUNG VA PHAN QUYEN RBAC
   ============================================================ */
IF OBJECT_ID('dbo.TaiKhoan', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TaiKhoan
    (
        MaTaiKhoan INT IDENTITY(1,1) NOT NULL,
        TenDangNhap VARCHAR(50) NOT NULL,
        MatKhauHash NVARCHAR(255) NOT NULL,
        HoTen NVARCHAR(100) NOT NULL,
        VaiTro VARCHAR(20) NOT NULL, -- 'Customer', 'Staff', 'Admin'
        SoDienThoai VARCHAR(15) NULL,
        Email VARCHAR(100) NULL,
        TrangThai VARCHAR(20) NOT NULL CONSTRAINT DF_TaiKhoan_TrangThai DEFAULT 'ACTIVE',
        NgayTao DATETIME2(0) NOT NULL CONSTRAINT DF_TaiKhoan_NgayTao DEFAULT SYSDATETIME(),

        CONSTRAINT PK_TaiKhoan PRIMARY KEY (MaTaiKhoan),
        CONSTRAINT UQ_TaiKhoan_TenDangNhap UNIQUE (TenDangNhap),
        CONSTRAINT CK_TaiKhoan_VaiTro CHECK (VaiTro IN ('Customer', 'Staff', 'Admin')),
        CONSTRAINT CK_TaiKhoan_TrangThai CHECK (TrangThai IN ('ACTIVE', 'LOCKED'))
    );
END;
GO

/* ============================================================
   BANG 2: VI TRI DO XE (40 O: 5 HANG A-E x 8 COT 1-8)
   Hien tai chi Hang E (E1-E8) la cam bien that tren Wokwi.
   Hang A, B, C, D la vi tri ao mo phong tren backend.
   ============================================================ */
IF OBJECT_ID('dbo.ViTriDoXe', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ViTriDoXe
    (
        MaViTri VARCHAR(4) NOT NULL,      -- 'A1'..'A8', 'B1'..'B8', 'C1'..'C8', 'D1'..'D8', 'E1'..'E8'
        TenViTri NVARCHAR(50) NOT NULL,
        Hang CHAR(1) NOT NULL,            -- 'A', 'B', 'C', 'D', 'E'
        Cot INT NOT NULL,                 -- 1..8
        LaViTriAo BIT NOT NULL CONSTRAINT DF_ViTriDoXe_LaViTriAo DEFAULT 0,
        TrangThai VARCHAR(20) NOT NULL CONSTRAINT DF_ViTriDoXe_TrangThai DEFAULT 'FREE',
        KhoangCachCM DECIMAL(8,2) NULL,
        LanCapNhat DATETIME2(0) NOT NULL CONSTRAINT DF_ViTriDoXe_LanCapNhat DEFAULT SYSDATETIME(),
        RowVersion ROWVERSION,

        CONSTRAINT PK_ViTriDoXe PRIMARY KEY (MaViTri),
        CONSTRAINT CK_ViTriDoXe_Hang CHECK (Hang IN ('A', 'B', 'C', 'D', 'E')),
        CONSTRAINT CK_ViTriDoXe_Cot CHECK (Cot BETWEEN 1 AND 8),
        CONSTRAINT CK_ViTriDoXe_TrangThai CHECK (
            TrangThai IN (
                'FREE', 'HELD', 'BOOKED', 'BOOKED_FUTURE', 
                'GUIDING', 'OCCUPIED', 'OVERLAP', 'UNKNOWN', 'MAINTENANCE'
            )
        )
    );
END;
GO

/* ============================================================
   BANG 3: DUONG DAN DEN NEOPIXEL (MAPPING CHO 40 O)
   ============================================================ */
IF OBJECT_ID('dbo.DuongDanDen', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DuongDanDen
    (
        MaViTri VARCHAR(4) NOT NULL,
        DanhSachLed NVARCHAR(255) NOT NULL, -- JSON array cac index den tren duong di vi du [0,1,2,3,16]
        PixelDich INT NOT NULL,              -- Index den tai ngay o do xe
        MauLedDich VARCHAR(20) NOT NULL CONSTRAINT DF_DuongDanDen_Mau DEFAULT '#00E5FF',

        CONSTRAINT PK_DuongDanDen PRIMARY KEY (MaViTri),
        CONSTRAINT FK_DuongDanDen_ViTriDoXe FOREIGN KEY (MaViTri) REFERENCES dbo.ViTriDoXe(MaViTri)
    );
END;
GO

/* ============================================================
   BANG 4: DON DAT CHO (ORDER) - THEO GIO & VANG LAI
   ============================================================ */
IF OBJECT_ID('dbo.DonDatCho', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DonDatCho
    (
        MaDon BIGINT IDENTITY(1,1) NOT NULL,
        MaTaiKhoan INT NOT NULL,
        LoaiKhach VARCHAR(20) NOT NULL CONSTRAINT DF_DonDatCho_LoaiKhach DEFAULT 'DAT_TRUOC', -- 'DAT_TRUOC', 'VANG_LAI'
        ThoiGianDat DATETIME2(0) NOT NULL CONSTRAINT DF_DonDatCho_ThoiGianDat DEFAULT SYSDATETIME(),
        ThoiGianHenT DATETIME2(0) NOT NULL,
        GoiThoiLuongGio INT NOT NULL, -- 1, 2, 4
        ThoiGianKetThucDuKien DATETIME2(0) NOT NULL,
        SoLuongXe INT NOT NULL CONSTRAINT DF_DonDatCho_SoLuongXe DEFAULT 1,
        TongTien DECIMAL(18,0) NOT NULL CONSTRAINT DF_DonDatCho_TongTien DEFAULT 0,
        TrangThai VARCHAR(25) NOT NULL CONSTRAINT DF_DonDatCho_TrangThai DEFAULT 'DRAFT',
        HanThanhToan DATETIME2(0) NULL,
        HanCheckIn DATETIME2(0) NULL,
        HanDoVaoO DATETIME2(0) NULL,
        SoLanGiaHan INT NOT NULL CONSTRAINT DF_DonDatCho_SoLanGiaHan DEFAULT 0,
        ThoiGianVaoCong DATETIME2(0) NULL,
        ThoiGianRaCong DATETIME2(0) NULL,
        TienPhat DECIMAL(18,0) NOT NULL CONSTRAINT DF_DonDatCho_TienPhat DEFAULT 0,
        GhiChu NVARCHAR(500) NULL,
        RowVersion ROWVERSION,

        CONSTRAINT PK_DonDatCho PRIMARY KEY (MaDon),
        CONSTRAINT FK_DonDatCho_TaiKhoan FOREIGN KEY (MaTaiKhoan) REFERENCES dbo.TaiKhoan(MaTaiKhoan),
        CONSTRAINT CK_DonDatCho_Goi CHECK (GoiThoiLuongGio IN (1, 2, 4)),
        CONSTRAINT CK_DonDatCho_TrangThai CHECK (
            TrangThai IN (
                'DRAFT', 'PENDING_PAYMENT', 'PAID', 'ENTERED', 
                'PARKED', 'COMPLETED', 'EXPIRED', 'NO_SHOW', 
                'ABANDONED', 'OVERSTAY', 'CANCELLED'
            )
        )
    );
END;
GO

/* ============================================================
   BANG 5: CHI TIET DON DAT CHO - VI TRI O & BIEN SO
   ============================================================ */
IF OBJECT_ID('dbo.DonDatCho_ViTri', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DonDatCho_ViTri
    (
        MaChiTiet BIGINT IDENTITY(1,1) NOT NULL,
        MaDon BIGINT NOT NULL,
        MaViTri VARCHAR(4) NOT NULL,
        BienSo VARCHAR(20) NOT NULL,
        TrangThai VARCHAR(20) NOT NULL CONSTRAINT DF_DonDatCho_ViTri_TrangThai DEFAULT 'ASSIGNED',
        ThoiGianVaoO DATETIME2(0) NULL,
        ThoiGianRaO DATETIME2(0) NULL,

        CONSTRAINT PK_DonDatCho_ViTri PRIMARY KEY (MaChiTiet),
        CONSTRAINT FK_DonDatCho_ViTri_Don FOREIGN KEY (MaDon) REFERENCES dbo.DonDatCho(MaDon),
        CONSTRAINT FK_DonDatCho_ViTri_ViTri FOREIGN KEY (MaViTri) REFERENCES dbo.ViTriDoXe(MaViTri),
        CONSTRAINT CK_DonDatCho_ViTri_TrangThai CHECK (
            TrangThai IN ('ASSIGNED', 'GUIDING', 'OCCUPIED', 'COMPLETED', 'CANCELLED')
        )
    );
END;
GO

/* ============================================================
   BANG 6: GIAO DICH THANH TOAN (PAYMENT)
   ============================================================ */
IF OBJECT_ID('dbo.ThanhToan', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ThanhToan
    (
        MaGiaoDich BIGINT IDENTITY(1,1) NOT NULL,
        MaDon BIGINT NOT NULL,
        HinhThuc VARCHAR(20) NOT NULL, -- 'CHUYEN_KHOAN', 'TIEN_MAT'
        SoTien DECIMAL(18,0) NOT NULL,
        TrangThai VARCHAR(20) NOT NULL CONSTRAINT DF_ThanhToan_TrangThai DEFAULT 'PENDING',
        MaGiaoDichDuyNhat VARCHAR(100) NOT NULL,
        ThoiGianTao DATETIME2(0) NOT NULL CONSTRAINT DF_ThanhToan_ThoiGianTao DEFAULT SYSDATETIME(),
        ThoiGianThanhToan DATETIME2(0) NULL,
        GhiChu NVARCHAR(255) NULL,

        CONSTRAINT PK_ThanhToan PRIMARY KEY (MaGiaoDich),
        CONSTRAINT FK_ThanhToan_Don FOREIGN KEY (MaDon) REFERENCES dbo.DonDatCho(MaDon),
        CONSTRAINT UQ_ThanhToan_MaGiaoDichDuyNhat UNIQUE (MaGiaoDichDuyNhat),
        CONSTRAINT CK_ThanhToan_HinhThuc CHECK (HinhThuc IN ('CHUYEN_KHOAN', 'TIEN_MAT')),
        CONSTRAINT CK_ThanhToan_TrangThai CHECK (TrangThai IN ('PENDING', 'PAID', 'FAILED', 'REFUNDED'))
    );
END;
GO

/* ============================================================
   BANG 7: MA XAC NHAN TIEN MAT (CASH CODE - SERVER KY HMAC-SHA256, DUNG 1 LAN)
   Bo ky tu: [2-9A-HJ-NP-Z] (Base32/Crockford khong gay nham lan giua 0/O, 1/I)
   Do dai: 6 ky tu ngau nhien (vd: CASH-7X9K2P).
   Khoa chinh: MaDon (1-1 voi DonDatCho). MaCashCodeHash luu ma bam HMAC-SHA256.
   ============================================================ */
IF OBJECT_ID('dbo.MaTienMat', 'U') IS NOT NULL AND COL_LENGTH('dbo.MaTienMat', 'MaCashCodeHash') IS NULL
BEGIN
    DROP TABLE dbo.MaTienMat;
END;
GO

IF OBJECT_ID('dbo.MaTienMat', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MaTienMat
    (
        MaDon BIGINT NOT NULL,
        MaCashCodeHash VARCHAR(64) NOT NULL,
        MaStaff INT NOT NULL,
        MaTaiKhoanCustomer INT NOT NULL,
        HanSuDung DATETIME2(0) NOT NULL,
        DaSuDung BIT NOT NULL CONSTRAINT DF_MaTienMat_DaSuDung DEFAULT 0,
        SoLanNhapSai INT NOT NULL CONSTRAINT DF_MaTienMat_SoLanNhapSai DEFAULT 0,
        BiKhoa BIT NOT NULL CONSTRAINT DF_MaTienMat_BiKhoa DEFAULT 0,
        ThoiGianSuDung DATETIME2(0) NULL,
        NgayTao DATETIME2(0) NOT NULL CONSTRAINT DF_MaTienMat_NgayTao DEFAULT SYSDATETIME(),

        CONSTRAINT PK_MaTienMat PRIMARY KEY (MaDon),
        CONSTRAINT FK_MaTienMat_Don FOREIGN KEY (MaDon) REFERENCES dbo.DonDatCho(MaDon) ON DELETE CASCADE,
        CONSTRAINT FK_MaTienMat_Staff FOREIGN KEY (MaStaff) REFERENCES dbo.TaiKhoan(MaTaiKhoan),
        CONSTRAINT FK_MaTienMat_Customer FOREIGN KEY (MaTaiKhoanCustomer) REFERENCES dbo.TaiKhoan(MaTaiKhoan)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_MaTienMat_MaDon' AND object_id = OBJECT_ID('dbo.MaTienMat'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UQ_MaTienMat_MaDon ON dbo.MaTienMat(MaDon);
END;
GO

/* ============================================================
   BANG 8: MA VAO CONG (ENTRY CODE / QR CODE)
   ============================================================ */
IF OBJECT_ID('dbo.MaVaoCong', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MaVaoCong
    (
        MaEntryCode VARCHAR(32) NOT NULL,
        MaDon BIGINT NOT NULL,
        BienSo VARCHAR(20) NOT NULL,
        HanSuDung DATETIME2(0) NOT NULL,
        TrangThai VARCHAR(20) NOT NULL CONSTRAINT DF_MaVaoCong_TrangThai DEFAULT 'ACTIVE',
        SoLanQuet INT NOT NULL CONSTRAINT DF_MaVaoCong_SoLanQuet DEFAULT 0,
        ThoiGianSuDung DATETIME2(0) NULL,
        NgayTao DATETIME2(0) NOT NULL CONSTRAINT DF_MaVaoCong_NgayTao DEFAULT SYSDATETIME(),

        CONSTRAINT PK_MaVaoCong PRIMARY KEY (MaEntryCode),
        CONSTRAINT FK_MaVaoCong_Don FOREIGN KEY (MaDon) REFERENCES dbo.DonDatCho(MaDon),
        CONSTRAINT CK_MaVaoCong_TrangThai CHECK (TrangThai IN ('ACTIVE', 'ENTERED', 'EXPIRED', 'CANCELLED'))
    );
END;
GO

/* ============================================================
   BANG 9: NHAT KY QUET MA VA MO CONG (SCAN LOG)
   ============================================================ */
IF OBJECT_ID('dbo.NhatKyQuetMa', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NhatKyQuetMa
    (
        MaNhatKy BIGINT IDENTITY(1,1) NOT NULL,
        MaEntryCode VARCHAR(32) NOT NULL,
        MaDon BIGINT NULL,
        LoaiCong VARCHAR(10) NOT NULL, -- 'IN', 'OUT'
        BienSoQuet VARCHAR(20) NULL,
        KetQua VARCHAR(20) NOT NULL,   -- 'SUCCESS', 'DENIED', 'EXPIRED', 'LIMIT_EXCEEDED'
        LyDo NVARCHAR(255) NULL,
        ThoiGianQuet DATETIME2(0) NOT NULL CONSTRAINT DF_NhatKyQuetMa_ThoiGian DEFAULT SYSDATETIME(),

        CONSTRAINT PK_NhatKyQuetMa PRIMARY KEY (MaNhatKy),
        CONSTRAINT CK_NhatKyQuetMa_LoaiCong CHECK (LoaiCong IN ('IN', 'OUT'))
    );
END;
GO

/* ============================================================
   BANG 10: NHAT KY HE THONG & CANH BAO
   ============================================================ */
IF OBJECT_ID('dbo.NhatKyHeThong', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NhatKyHeThong
    (
        MaNhatKy BIGINT IDENTITY(1,1) NOT NULL,
        MaDon BIGINT NULL,
        MaViTri VARCHAR(4) NULL,
        LoaiSuKien VARCHAR(60) NOT NULL,
        NguonSuKien VARCHAR(20) NOT NULL, -- 'ESP32', 'DASHBOARD', 'BACKEND', 'SYSTEM'
        MucDo VARCHAR(10) NOT NULL CONSTRAINT DF_NhatKyHeThong_MucDo DEFAULT 'INFO',
        MQTTTopic VARCHAR(200) NULL,
        NoiDung NVARCHAR(MAX) NULL,
        ThoiGianGhiNhan DATETIME2(0) NOT NULL CONSTRAINT DF_NhatKyHeThong_ThoiGian DEFAULT SYSDATETIME(),

        CONSTRAINT PK_NhatKyHeThong PRIMARY KEY (MaNhatKy),
        CONSTRAINT CK_NhatKyHeThong_MucDo CHECK (MucDo IN ('INFO', 'WARNING', 'ERROR'))
    );
END;
GO

/* ============================================================
   BANG 11: LICH SU CAM BIEN DO XE
   ============================================================ */
IF OBJECT_ID('dbo.LichSuCamBien', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LichSuCamBien
    (
        MaLichSu BIGINT IDENTITY(1,1) NOT NULL,
        MaViTri VARCHAR(4) NOT NULL,
        KhoangCachCM DECIMAL(8,2) NULL,
        TrangThaiCu VARCHAR(20) NULL,
        TrangThaiMoi VARCHAR(20) NOT NULL,
        ThoiGianGhiNhan DATETIME2(0) NOT NULL CONSTRAINT DF_LichSuCamBien_ThoiGian DEFAULT SYSDATETIME(),

        CONSTRAINT PK_LichSuCamBien PRIMARY KEY (MaLichSu),
        CONSTRAINT FK_LichSuCamBien_ViTri FOREIGN KEY (MaViTri) REFERENCES dbo.ViTriDoXe(MaViTri)
    );
END;
GO

/* ============================================================
   DON DEP BANG CU KHONG CON DUNG (LEGACY)
   ============================================================ */
IF OBJECT_ID('dbo.MqttReliableEvent', 'U') IS NOT NULL DROP TABLE dbo.MqttReliableEvent;
IF OBJECT_ID('dbo.MqttEventInbox', 'U') IS NOT NULL DROP TABLE dbo.MqttEventInbox;
GO

/* ============================================================
   12. BANG THIET BI (ESP32 NODES: GATE + 5 ROWS)
   ============================================================ */
IF OBJECT_ID('dbo.ThietBi', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ThietBi
    (
        MaThietBi VARCHAR(50) NOT NULL,
        Loai VARCHAR(20) NOT NULL CONSTRAINT DF_ThietBi_Test_Loai DEFAULT 'ROW',
        HangO VARCHAR(4) NULL,
        PixelOffset INT NOT NULL CONSTRAINT DF_ThietBi_Test_PixelOffset DEFAULT 0,
        PixelCount INT NOT NULL CONSTRAINT DF_ThietBi_Test_PixelCount DEFAULT 8,
        TrangThai VARCHAR(20) NOT NULL CONSTRAINT DF_ThietBi_Test_TrangThai DEFAULT 'ONLINE',
        LanCapNhat DATETIME2(0) NULL CONSTRAINT DF_ThietBi_Test_LanCapNhat DEFAULT SYSDATETIME(),

        CONSTRAINT PK_ThietBi_Test PRIMARY KEY (MaThietBi)
    );
END;
GO

/* ============================================================
   CHI MUC (INDEXES) TOI UU TRUY VAN (CO IF NOT EXISTS CHO PHEP CHAY LAI NHIEU LAN)
   ============================================================ */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DonDatCho_TrangThai_HenT' AND object_id = OBJECT_ID('dbo.DonDatCho'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DonDatCho_TrangThai_HenT ON dbo.DonDatCho (TrangThai, ThoiGianHenT);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DonDatCho_HanCheckIn' AND object_id = OBJECT_ID('dbo.DonDatCho'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DonDatCho_HanCheckIn ON dbo.DonDatCho (HanCheckIn) WHERE TrangThai = 'PAID';
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DonDatCho_HanDoVaoO' AND object_id = OBJECT_ID('dbo.DonDatCho'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DonDatCho_HanDoVaoO ON dbo.DonDatCho (HanDoVaoO) WHERE TrangThai = 'ENTERED';
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DonDatCho_HanThanhToan' AND object_id = OBJECT_ID('dbo.DonDatCho'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DonDatCho_HanThanhToan ON dbo.DonDatCho (HanThanhToan) WHERE TrangThai = 'PENDING_PAYMENT';
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DonDatCho_ViTri_ViTri' AND object_id = OBJECT_ID('dbo.DonDatCho_ViTri'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_DonDatCho_ViTri_ViTri ON dbo.DonDatCho_ViTri (MaViTri, TrangThai);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_NhatKyQuetMa_EntryCode' AND object_id = OBJECT_ID('dbo.NhatKyQuetMa'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_NhatKyQuetMa_EntryCode ON dbo.NhatKyQuetMa (MaEntryCode, ThoiGianQuet);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_NhatKyHeThong_MucDo_ThoiGian' AND object_id = OBJECT_ID('dbo.NhatKyHeThong'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_NhatKyHeThong_MucDo_ThoiGian ON dbo.NhatKyHeThong (MucDo, ThoiGianGhiNhan DESC);
END;
GO

-- Kiem tra cot RowVersion tren DonDatCho cho co so du lieu da ton tai
IF COL_LENGTH('dbo.DonDatCho', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.DonDatCho ADD RowVersion ROWVERSION;
END;
GO

/* ============================================================
   SEED DATA: TAI KHOAN MAU (ADMIN, STAFF, CUSTOMER)
   Mat khau mac dinh:
   - admin: Admin@123
   - staff01: Staff@123
   - customer01: Customer@123
   - customer02: Customer02@123
   ============================================================ */
IF NOT EXISTS (SELECT 1 FROM dbo.TaiKhoan WHERE TenDangNhap = 'admin')
BEGIN
    INSERT INTO dbo.TaiKhoan (TenDangNhap, MatKhauHash, HoTen, VaiTro, SoDienThoai, Email)
    VALUES ('admin', 'PBKDF2$SHA256$100000$6FEF607BE9F94E52AA999064FEE2DB6C$8F850E01B367299AAEC335644374003B03D575E5FBB3BD1769DA75B3A184B284', N'Quản trị viên Hệ thống', 'Admin', '0901234567', 'admin@smartparking.local');
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.TaiKhoan WHERE TenDangNhap = 'staff01')
BEGIN
    INSERT INTO dbo.TaiKhoan (TenDangNhap, MatKhauHash, HoTen, VaiTro, SoDienThoai, Email)
    VALUES ('staff01', 'PBKDF2$SHA256$100000$715429ADDDD91FFD5003A0F3AAD1F8C6$6BE8B442206141C4FD67166488F39B6D9305D5B9344766387B45724568571217', N'Nhân viên Trực bãi', 'Staff', '0912345678', 'staff01@smartparking.local');
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.TaiKhoan WHERE TenDangNhap = 'customer01')
BEGIN
    INSERT INTO dbo.TaiKhoan (TenDangNhap, MatKhauHash, HoTen, VaiTro, SoDienThoai, Email)
    VALUES ('customer01', 'PBKDF2$SHA256$100000$58C958D1D598D7F0BA2C9775C54E770B$29663DA0263467058CD89B039F1782F94F38D8455B4C4A5884B538F9FAD2C9FD', N'Khách hàng Demo', 'Customer', '0987654321', 'customer01@gmail.com');
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.TaiKhoan WHERE TenDangNhap = 'customer02')
BEGIN
    INSERT INTO dbo.TaiKhoan (TenDangNhap, MatKhauHash, HoTen, VaiTro, SoDienThoai, Email)
    VALUES ('customer02', 'PBKDF2$SHA256$100000$58C958D1D598D7F0BA2C9775C54E770B$29663DA0263467058CD89B039F1782F94F38D8455B4C4A5884B538F9FAD2C9FD', N'Khách hàng Demo 02', 'Customer', '0987654322', 'customer02@smartparking.local');
END;
GO

/* ============================================================
   SEED DATA: 40 VI TRI DO XE (A1-A8, B1-B8, C1-C8, D1-D8, E1-E8)
   Hang E la cam bien that Wokwi (LaViTriAo = 0).
   Hang A, B, C, D la cam bien ao (LaViTriAo = 1).
   ============================================================ */
DECLARE @rows TABLE (Hang CHAR(1), LaAo BIT);
INSERT INTO @rows (Hang, LaAo) VALUES ('A', 1), ('B', 1), ('C', 1), ('D', 1), ('E', 0);

DECLARE @c INT = 1;
WHILE @c <= 8
BEGIN
    INSERT INTO dbo.ViTriDoXe (MaViTri, TenViTri, Hang, Cot, LaViTriAo, TrangThai)
    SELECT 
        R.Hang + CAST(@c AS VARCHAR(2)),
        N'Vị trí ' + R.Hang + CAST(@c AS VARCHAR(2)) + CASE WHEN R.LaAo = 1 THEN N' (Ảo)' ELSE N' (Cảm biến thật)' END,
        R.Hang,
        @c,
        R.LaAo,
        'FREE'
    FROM @rows R
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ViTriDoXe WHERE MaViTri = R.Hang + CAST(@c AS VARCHAR(2)));

    SET @c = @c + 1;
END;
GO

/* ============================================================
   SEED DATA: DUONG DAN DEN NEOPIXEL CHO 40 O
   Mapping logic: 
   - Aisle den chinh: LED 0 -> 1 -> 2 -> 3 -> 4 (CP1) -> 5 -> 6 (CP2) -> 7
   - Cac o do co LED chi thi rieng:
     Hang E: E1->E8 map vao pixels 10..17, dich den sang mau Cyan #00E5FF
     Hang D: D1->D8 map vao pixels 18..25, dich den sang mau Cyan #00E5FF
     Hang C: C1->C8 map vao pixels 26..33, dich den sang mau Cyan #00E5FF
     Hang B: B1->B8 map vao pixels 34..41, dich den sang mau Cyan #00E5FF
     Hang A: A1->A8 map vao pixels 42..49, dich den sang mau Cyan #00E5FF
   ============================================================ */
-- Hang E (that)
IF NOT EXISTS (SELECT 1 FROM dbo.DuongDanDen WHERE MaViTri = 'E1')
BEGIN
    INSERT INTO dbo.DuongDanDen (MaViTri, DanhSachLed, PixelDich, MauLedDich) VALUES
    ('E1', '[0, 1, 2, 10]', 10, '#00E5FF'),
    ('E2', '[0, 1, 2, 11]', 11, '#00E5FF'),
    ('E3', '[0, 1, 2, 3, 12]', 12, '#00E5FF'),
    ('E4', '[0, 1, 2, 3, 13]', 13, '#00E5FF'),
    ('E5', '[0, 1, 4, 5, 14]', 14, '#00E5FF'),
    ('E6', '[0, 1, 4, 5, 15]', 15, '#00E5FF'),
    ('E7', '[0, 1, 4, 5, 6, 16]', 16, '#00E5FF'),
    ('E8', '[0, 1, 4, 5, 6, 17]', 17, '#00E5FF');
END;

-- Hang D (ao)
IF NOT EXISTS (SELECT 1 FROM dbo.DuongDanDen WHERE MaViTri = 'D1')
BEGIN
    INSERT INTO dbo.DuongDanDen (MaViTri, DanhSachLed, PixelDich, MauLedDich) VALUES
    ('D1', '[0, 1, 2, 18]', 18, '#00E5FF'),
    ('D2', '[0, 1, 2, 19]', 19, '#00E5FF'),
    ('D3', '[0, 1, 2, 3, 20]', 20, '#00E5FF'),
    ('D4', '[0, 1, 2, 3, 21]', 21, '#00E5FF'),
    ('D5', '[0, 1, 4, 5, 22]', 22, '#00E5FF'),
    ('D6', '[0, 1, 4, 5, 23]', 23, '#00E5FF'),
    ('D7', '[0, 1, 4, 5, 6, 24]', 24, '#00E5FF'),
    ('D8', '[0, 1, 4, 5, 6, 25]', 25, '#00E5FF');
END;

-- Hang C (ao)
IF NOT EXISTS (SELECT 1 FROM dbo.DuongDanDen WHERE MaViTri = 'C1')
BEGIN
    INSERT INTO dbo.DuongDanDen (MaViTri, DanhSachLed, PixelDich, MauLedDich) VALUES
    ('C1', '[0, 1, 2, 26]', 26, '#00E5FF'),
    ('C2', '[0, 1, 2, 27]', 27, '#00E5FF'),
    ('C3', '[0, 1, 2, 3, 28]', 28, '#00E5FF'),
    ('C4', '[0, 1, 2, 3, 29]', 29, '#00E5FF'),
    ('C5', '[0, 1, 4, 5, 30]', 30, '#00E5FF'),
    ('C6', '[0, 1, 4, 5, 31]', 31, '#00E5FF'),
    ('C7', '[0, 1, 4, 5, 6, 32]', 32, '#00E5FF'),
    ('C8', '[0, 1, 4, 5, 6, 33]', 33, '#00E5FF');
END;

-- Hang B (ao)
IF NOT EXISTS (SELECT 1 FROM dbo.DuongDanDen WHERE MaViTri = 'B1')
BEGIN
    INSERT INTO dbo.DuongDanDen (MaViTri, DanhSachLed, PixelDich, MauLedDich) VALUES
    ('B1', '[0, 1, 2, 34]', 34, '#00E5FF'),
    ('B2', '[0, 1, 2, 35]', 35, '#00E5FF'),
    ('B3', '[0, 1, 2, 3, 36]', 36, '#00E5FF'),
    ('B4', '[0, 1, 2, 3, 37]', 37, '#00E5FF'),
    ('B5', '[0, 1, 4, 5, 38]', 38, '#00E5FF'),
    ('B6', '[0, 1, 4, 5, 39]', 39, '#00E5FF'),
    ('B7', '[0, 1, 4, 5, 6, 40]', 40, '#00E5FF'),
    ('B8', '[0, 1, 4, 5, 6, 41]', 41, '#00E5FF');
END;

-- Hang A (ao)
IF NOT EXISTS (SELECT 1 FROM dbo.DuongDanDen WHERE MaViTri = 'A1')
BEGIN
    INSERT INTO dbo.DuongDanDen (MaViTri, DanhSachLed, PixelDich, MauLedDich) VALUES
    ('A1', '[0, 1, 2, 42]', 42, '#00E5FF'),
    ('A2', '[0, 1, 2, 43]', 43, '#00E5FF'),
    ('A3', '[0, 1, 2, 3, 44]', 44, '#00E5FF'),
    ('A4', '[0, 1, 2, 3, 45]', 45, '#00E5FF'),
    ('A5', '[0, 1, 4, 5, 46]', 46, '#00E5FF'),
    ('A6', '[0, 1, 4, 5, 47]', 47, '#00E5FF'),
    ('A7', '[0, 1, 4, 5, 6, 48]', 48, '#00E5FF'),
    ('A8', '[0, 1, 4, 5, 6, 49]', 49, '#00E5FF');
END;
GO


-- Seed 6 thiet bi ESP32 (1 gate, 5 hang o)
IF NOT EXISTS (SELECT 1 FROM dbo.ThietBi WHERE MaThietBi = 'esp32_gate')
BEGIN
    INSERT INTO dbo.ThietBi (MaThietBi, Loai, HangO, PixelOffset, PixelCount, TrangThai, LanCapNhat) VALUES
    ('esp32_gate', 'GATE', NULL, 0, 0, 'ONLINE', SYSDATETIME()),
    ('esp32_row_e', 'ROW', 'E', 10, 8, 'ONLINE', SYSDATETIME()),
    ('esp32_row_d', 'ROW', 'D', 18, 8, 'ONLINE', SYSDATETIME()),
    ('esp32_row_c', 'ROW', 'C', 26, 8, 'ONLINE', SYSDATETIME()),
    ('esp32_row_b', 'ROW', 'B', 34, 8, 'ONLINE', SYSDATETIME()),
    ('esp32_row_a', 'ROW', 'A', 42, 8, 'ONLINE', SYSDATETIME());
END;
GO
PRINT N'HOAN TAT KHOI TAO SCHEMA VA SEED DATA CHO QL_BAIGIUXE_IOT_TEST.';
GO


SELECT COUNT(*) FROM ViTriDoXe;                         -- mong đợi 40
SELECT TenDangNhap, VaiTro, LEFT(MatKhauHash,12) FROM TaiKhoan;  -- 3 tài khoản, mật khẩu phải là chuỗi băm
SELECT COUNT(*) FROM DuongDanDen;                       -- mong đợi 40 ô có đường đi


SELECT COUNT(*) AS SoBang FROM sys.tables;   -- mong đợi 12 (nếu agent tính cả bảng cũ thì có thể nhiều hơn, so với danh sách agent liệt kê)

SELECT i.name, COUNT(*) AS SoLan
FROM sys.indexes i
WHERE i.name IN ('IX_DonDatCho_TrangThai_HenT','IX_DonDatCho_HanCheckIn','IX_DonDatCho_HanDoVaoO',
                 'IX_DonDatCho_HanThanhToan','IX_DonDatCho_ViTri_ViTri','IX_NhatKyQuetMa_EntryCode',
                 'IX_NhatKyHeThong_MucDo_ThoiGian')
GROUP BY i.name;   