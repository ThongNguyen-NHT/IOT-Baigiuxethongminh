using System;
using Microsoft.EntityFrameworkCore;
using SmartParking.Api.Models;

namespace SmartParking.Api.Data;

public partial class SmartParkingDbContext : DbContext
{
    public SmartParkingDbContext(DbContextOptions<SmartParkingDbContext> options)
        : base(options)
    {
    }

    // Core New Entities (Phase 1)
    public virtual DbSet<TaiKhoan> TaiKhoans { get; set; } = null!;
    public virtual DbSet<ThietBi> ThietBis { get; set; } = null!;
    public virtual DbSet<ViTriDoXe> ViTriDoXes { get; set; } = null!;
    public virtual DbSet<DuongDanDen> DuongDanDens { get; set; } = null!;
    public virtual DbSet<DonDatCho> DonDatChos { get; set; } = null!;
    public virtual DbSet<DonDatChoViTri> DonDatChoViTris { get; set; } = null!;
    public virtual DbSet<ThanhToan> ThanhToans { get; set; } = null!;
    public virtual DbSet<MaTienMat> MaTienMats { get; set; } = null!;
    public virtual DbSet<MaVaoCong> MaVaoCongs { get; set; } = null!;
    public virtual DbSet<NhatKyQuetMa> NhatKyQuetMas { get; set; } = null!;
    public virtual DbSet<NhatKyHeThong> NhatKyHeThongs { get; set; } = null!;
    public virtual DbSet<LichSuCamBien> LichSuCamBiens { get; set; } = null!;

    // Legacy tables preserved for gradual migration
    public virtual DbSet<ChiTietViTriDo> ChiTietViTriDos { get; set; } = null!;
    public virtual DbSet<PhienGuiXe> PhienGuiXes { get; set; } = null!;
    public virtual DbSet<PhuongTien> PhuongTiens { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 1. TaiKhoan
        modelBuilder.Entity<TaiKhoan>(entity =>
        {
            entity.HasIndex(e => e.TenDangNhap).IsUnique();
        });

        // 2. ViTriDoXe
        modelBuilder.Entity<ViTriDoXe>(entity =>
        {
            entity.Property(e => e.TrangThai).HasDefaultValue("FREE");
            entity.Property(e => e.KhoangCachCm).HasPrecision(8, 2);
            entity.Property(e => e.LanCapNhat).HasDefaultValueSql("sysdatetime()");
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.HasOne(e => e.DuongDanDen)
                .WithOne(e => e.ViTriDoXe)
                .HasForeignKey<DuongDanDen>(e => e.MaViTri)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 3. DuongDanDen
        modelBuilder.Entity<DuongDanDen>(entity =>
        {
            entity.Property(e => e.MauLedDich).HasDefaultValue("#00E5FF");
        });

        // 4. DonDatCho
        modelBuilder.Entity<DonDatCho>(entity =>
        {
            entity.Property(e => e.LoaiKhach).HasDefaultValue("DAT_TRUOC");
            entity.Property(e => e.TrangThai).HasDefaultValue("DRAFT");
            entity.Property(e => e.TongTien).HasPrecision(18, 0);
            entity.Property(e => e.TienPhat).HasPrecision(18, 0).HasDefaultValue(0m);
            entity.Property(e => e.ThoiGianDat).HasDefaultValueSql("sysdatetime()");
            entity.Property(e => e.SoLuongXe).HasDefaultValue(1);
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.HasOne(e => e.TaiKhoan)
                .WithMany(p => p.DonDatChos)
                .HasForeignKey(e => e.MaTaiKhoan)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.TrangThai, e.ThoiGianHenT });
            entity.HasIndex(e => e.HanCheckIn).HasFilter("[TrangThai] = 'PAID'");
            entity.HasIndex(e => e.HanDoVaoO).HasFilter("[TrangThai] = 'ENTERED'");
            entity.HasIndex(e => e.HanThanhToan).HasFilter("[TrangThai] = 'PENDING_PAYMENT'");
        });

        // 5. DonDatCho_ViTri
        modelBuilder.Entity<DonDatChoViTri>(entity =>
        {
            entity.Property(e => e.TrangThai).HasDefaultValue("ASSIGNED");

            entity.HasOne(e => e.DonDatCho)
                .WithMany(p => p.DonDatChoViTris)
                .HasForeignKey(e => e.MaDon)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ViTriDoXe)
                .WithMany(p => p.DonDatChoViTris)
                .HasForeignKey(e => e.MaViTri)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.MaViTri, e.TrangThai });
        });

        // 6. ThanhToan
        modelBuilder.Entity<ThanhToan>(entity =>
        {
            entity.HasIndex(e => e.MaGiaoDichDuyNhat).IsUnique();
            entity.Property(e => e.SoTien).HasPrecision(18, 0);
            entity.Property(e => e.TrangThai).HasDefaultValue("PENDING");
            entity.Property(e => e.ThoiGianTao).HasDefaultValueSql("sysdatetime()");

            entity.HasOne(e => e.DonDatCho)
                .WithMany(p => p.ThanhToans)
                .HasForeignKey(e => e.MaDon)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 7. MaTienMat
        modelBuilder.Entity<MaTienMat>(entity =>
        {
            entity.HasKey(e => e.MaDon);
            entity.Property(e => e.DaSuDung).HasDefaultValue(false);
            entity.Property(e => e.SoLanNhapSai).HasDefaultValue(0);
            entity.Property(e => e.BiKhoa).HasDefaultValue(false);
            entity.Property(e => e.NgayTao).HasDefaultValueSql("sysdatetime()");

            entity.HasOne(e => e.DonDatCho)
                .WithOne(p => p.MaTienMat)
                .HasForeignKey<MaTienMat>(e => e.MaDon)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Staff)
                .WithMany(p => p.MaTienMatsDaCap)
                .HasForeignKey(e => e.MaStaff)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Customer)
                .WithMany(p => p.MaTienMatsKhach)
                .HasForeignKey(e => e.MaTaiKhoanCustomer)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.MaDon).IsUnique();
        });

        // 8. MaVaoCong
        modelBuilder.Entity<MaVaoCong>(entity =>
        {
            entity.Property(e => e.TrangThai).HasDefaultValue("ACTIVE");
            entity.Property(e => e.SoLanQuet).HasDefaultValue(0);
            entity.Property(e => e.NgayTao).HasDefaultValueSql("sysdatetime()");

            entity.HasOne(e => e.DonDatCho)
                .WithMany(p => p.MaVaoCongs)
                .HasForeignKey(e => e.MaDon)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 9. NhatKyQuetMa
        modelBuilder.Entity<NhatKyQuetMa>(entity =>
        {
            entity.Property(e => e.ThoiGianQuet).HasDefaultValueSql("sysdatetime()");
            entity.HasIndex(e => new { e.MaEntryCode, e.ThoiGianQuet });
        });

        // 10. NhatKyHeThong
        modelBuilder.Entity<NhatKyHeThong>(entity =>
        {
            entity.Property(e => e.MucDo).HasDefaultValue("INFO");
            entity.Property(e => e.ThoiGianGhiNhan).HasDefaultValueSql("sysdatetime()");
            entity.HasIndex(e => new { e.MucDo, e.ThoiGianGhiNhan });
        });

        // 11. LichSuCamBien
        modelBuilder.Entity<LichSuCamBien>(entity =>
        {
            entity.Property(e => e.KhoangCachCm).HasPrecision(8, 2);
            entity.Property(e => e.ThoiGianGhiNhan).HasDefaultValueSql("sysdatetime()");
            entity.HasOne(e => e.MaViTriNavigation)
                .WithMany(p => p.LichSuCamBiens)
                .HasForeignKey(e => e.MaViTri)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 12. ThietBi
        modelBuilder.Entity<ThietBi>(entity =>
        {
            entity.ToTable("ThietBi");
            entity.HasKey(e => e.MaThietBi);
            entity.Property(e => e.Loai).HasDefaultValue("ROW");
            entity.Property(e => e.PixelOffset).HasDefaultValue(0);
            entity.Property(e => e.PixelCount).HasDefaultValue(8);
            entity.Property(e => e.TrangThai).HasDefaultValue("ONLINE");
            entity.Property(e => e.LanCapNhat).HasDefaultValueSql("sysdatetime()");
        });

        // Legacy: ChiTietViTriDo & PhienGuiXe & PhuongTien
        modelBuilder.Entity<ChiTietViTriDo>(entity =>
        {
            entity.ToTable("ChiTietViTriDo");
            entity.Property(e => e.ThoiGianBatDau).HasDefaultValueSql("sysdatetime()");
        });

        modelBuilder.Entity<PhienGuiXe>(entity =>
        {
            entity.ToTable("PhienGuiXe");
            entity.Property(e => e.DonGiaMoiPhut).HasPrecision(18, 0).HasDefaultValue(2000m);
            entity.Property(e => e.TongTien).HasPrecision(18, 0);
            entity.Property(e => e.ThoiGianVao).HasDefaultValueSql("sysdatetime()");
            entity.Property(e => e.TrangThai).HasDefaultValue("PARKING");
        });

        modelBuilder.Entity<PhuongTien>(entity =>
        {
            entity.ToTable("PhuongTien");
            entity.Property(e => e.NgayTao).HasDefaultValueSql("sysdatetime()");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
