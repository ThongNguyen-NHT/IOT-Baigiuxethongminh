using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("NhatKyQuetMa")]
public class NhatKyQuetMa
{
    [Key]
    public long MaNhatKy { get; set; }

    [Required]
    [MaxLength(32)]
    public string MaEntryCode { get; set; } = string.Empty;

    public long? MaDon { get; set; }

    [Required]
    [MaxLength(10)]
    public string LoaiCong { get; set; } = "IN"; // IN, OUT

    [MaxLength(20)]
    public string? BienSoQuet { get; set; }

    [Required]
    [MaxLength(20)]
    public string KetQua { get; set; } = "SUCCESS"; // SUCCESS, DENIED, EXPIRED, LIMIT_EXCEEDED

    [MaxLength(255)]
    public string? LyDo { get; set; }

    public DateTime ThoiGianQuet { get; set; } = DateTime.UtcNow;
}
