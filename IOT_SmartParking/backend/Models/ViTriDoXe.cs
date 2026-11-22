using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("ViTriDoXe")]
public class ViTriDoXe
{
    [Key]
    [MaxLength(4)]
    public string MaViTri { get; set; } = string.Empty; // A1..A8, B1..B8, C1..C8, D1..D8, E1..E8

    [Required]
    [MaxLength(50)]
    public string TenViTri { get; set; } = string.Empty;

    [Required]
    [MaxLength(1)]
    public string Hang { get; set; } = string.Empty; // A, B, C, D, E

    public int Cot { get; set; } // 1..8

    public bool LaViTriAo { get; set; } // True if A-D, False if E (Real sensors on Wokwi)

    [Required]
    [MaxLength(20)]
    public string TrangThai { get; set; } = "FREE";
    // FREE, HELD, BOOKED, BOOKED_FUTURE, GUIDING, OCCUPIED, OVERLAP, UNKNOWN, MAINTENANCE

    public decimal? KhoangCachCm { get; set; }

    public DateTime LanCapNhat { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public virtual DuongDanDen? DuongDanDen { get; set; }
    public virtual ICollection<DonDatChoViTri> DonDatChoViTris { get; set; } = new List<DonDatChoViTri>();
    public virtual ICollection<LichSuCamBien> LichSuCamBiens { get; set; } = new List<LichSuCamBien>();
}
