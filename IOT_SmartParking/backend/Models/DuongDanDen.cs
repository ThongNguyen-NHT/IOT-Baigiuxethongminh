using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("DuongDanDen")]
public class DuongDanDen
{
    [Key]
    [MaxLength(4)]
    public string MaViTri { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string DanhSachLed { get; set; } = "[]"; // JSON array ví dụ "[0, 1, 2, 10]"

    public int PixelDich { get; set; }

    [Required]
    [MaxLength(20)]
    public string MauLedDich { get; set; } = "#00E5FF";

    [ForeignKey(nameof(MaViTri))]
    public virtual ViTriDoXe ViTriDoXe { get; set; } = null!;
}
