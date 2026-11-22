using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("ThanhToan")]
public class ThanhToan
{
    [Key]
    public long MaGiaoDich { get; set; }

    public long MaDon { get; set; }

    [Required]
    [MaxLength(20)]
    public string HinhThuc { get; set; } = "CHUYEN_KHOAN"; // CHUYEN_KHOAN, TIEN_MAT

    public decimal SoTien { get; set; }

    [Required]
    [MaxLength(20)]
    public string TrangThai { get; set; } = "PENDING"; // PENDING, PAID, FAILED, REFUNDED

    [Required]
    [MaxLength(100)]
    public string MaGiaoDichDuyNhat { get; set; } = string.Empty;

    public DateTime ThoiGianTao { get; set; } = DateTime.UtcNow;

    public DateTime? ThoiGianThanhToan { get; set; }

    [MaxLength(255)]
    public string? GhiChu { get; set; }

    [ForeignKey(nameof(MaDon))]
    public virtual DonDatCho DonDatCho { get; set; } = null!;
}
