using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("DonDatCho")]
public class DonDatCho
{
    [Key]
    public long MaDon { get; set; }

    public int MaTaiKhoan { get; set; }

    [Required]
    [MaxLength(20)]
    public string LoaiKhach { get; set; } = "DAT_TRUOC"; // DAT_TRUOC, VANG_LAI

    public DateTime ThoiGianDat { get; set; } = DateTime.UtcNow;

    public DateTime ThoiGianHenT { get; set; }

    public int GoiThoiLuongGio { get; set; } // 1, 2, 4

    public DateTime ThoiGianKetThucDuKien { get; set; }

    public int SoLuongXe { get; set; } = 1;

    public decimal TongTien { get; set; }

    [Required]
    [MaxLength(25)]
    public string TrangThai { get; set; } = "DRAFT";
    // DRAFT, PENDING_PAYMENT, PAID, ENTERED, PARKED, COMPLETED, EXPIRED, NO_SHOW, ABANDONED, OVERSTAY, CANCELLED

    public DateTime? HanThanhToan { get; set; }
    public DateTime? HanCheckIn { get; set; }
    public DateTime? HanDoVaoO { get; set; }
    public int SoLanGiaHan { get; set; } = 0;

    public DateTime? ThoiGianVaoCong { get; set; }
    public DateTime? ThoiGianRaCong { get; set; }
    public decimal TienPhat { get; set; } = 0;

    [MaxLength(500)]
    public string? GhiChu { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    [ForeignKey(nameof(MaTaiKhoan))]
    public virtual TaiKhoan TaiKhoan { get; set; } = null!;

    public virtual ICollection<DonDatChoViTri> DonDatChoViTris { get; set; } = new List<DonDatChoViTri>();
    public virtual ICollection<ThanhToan> ThanhToans { get; set; } = new List<ThanhToan>();
    public virtual MaTienMat? MaTienMat { get; set; }
    public virtual ICollection<MaVaoCong> MaVaoCongs { get; set; } = new List<MaVaoCong>();
}
