using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("DonDatCho_ViTri")]
public class DonDatChoViTri
{
    [Key]
    public long MaChiTiet { get; set; }

    public long MaDon { get; set; }

    [Required]
    [MaxLength(4)]
    public string MaViTri { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string BienSo { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string TrangThai { get; set; } = "ASSIGNED"; // ASSIGNED, GUIDING, OCCUPIED, COMPLETED, CANCELLED

    public DateTime? ThoiGianVaoO { get; set; }
    public DateTime? ThoiGianRaO { get; set; }

    [ForeignKey(nameof(MaDon))]
    public virtual DonDatCho DonDatCho { get; set; } = null!;

    [ForeignKey(nameof(MaViTri))]
    public virtual ViTriDoXe ViTriDoXe { get; set; } = null!;
}
