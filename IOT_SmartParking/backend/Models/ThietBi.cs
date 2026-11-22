using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("ThietBi")]
public class ThietBi
{
    [Key]
    [StringLength(50)]
    public string MaThietBi { get; set; } = string.Empty; // esp32_gate, esp32_row_a, ...

    [StringLength(20)]
    public string Loai { get; set; } = "ROW"; // GATE, ROW

    [StringLength(4)]
    public string? HangO { get; set; } // null cho GATE, 'A', 'B', 'C', 'D', 'E' cho ROW

    public int PixelOffset { get; set; } = 0;

    public int PixelCount { get; set; } = 8;

    [StringLength(20)]
    public string TrangThai { get; set; } = "ONLINE"; // ONLINE, OFFLINE

    public DateTime? LanCapNhat { get; set; } = DateTime.UtcNow;
}
