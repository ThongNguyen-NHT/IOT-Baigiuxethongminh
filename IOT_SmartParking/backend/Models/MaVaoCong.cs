using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("MaVaoCong")]
public class MaVaoCong
{
    [Key]
    [MaxLength(32)]
    public string MaEntryCode { get; set; } = string.Empty;

    public long MaDon { get; set; }

    [Required]
    [MaxLength(20)]
    public string BienSo { get; set; } = string.Empty;

    public DateTime HanSuDung { get; set; }

    [Required]
    [MaxLength(20)]
    public string TrangThai { get; set; } = "ACTIVE"; // ACTIVE, ENTERED, EXPIRED, CANCELLED

    public int SoLanQuet { get; set; } = 0;

    public DateTime? ThoiGianSuDung { get; set; }

    public DateTime NgayTao { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(MaDon))]
    public virtual DonDatCho DonDatCho { get; set; } = null!;
}
