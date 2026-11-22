using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SmartParking.Api.Models;

[Table("LichSuCamBien")]
[Index("MaViTri", "ThoiGianGhiNhan", Name = "IX_LichSuCamBien_ViTri_ThoiGian", IsDescending = new[] { false, true })]
public partial class LichSuCamBien
{
    [Key]
    public long MaLichSu { get; set; }

    [StringLength(4)]
    [Unicode(false)]
    public string MaViTri { get; set; } = null!;

    [Column("KhoangCachCM", TypeName = "decimal(8, 2)")]
    public decimal? KhoangCachCm { get; set; }

    [StringLength(20)]
    [Unicode(false)]
    public string? TrangThaiCu { get; set; }

    [StringLength(20)]
    [Unicode(false)]
    public string TrangThaiMoi { get; set; } = null!;

    [Precision(0)]
    public DateTime ThoiGianGhiNhan { get; set; } = DateTime.UtcNow;

    [ForeignKey("MaViTri")]
    [InverseProperty("LichSuCamBiens")]
    public virtual ViTriDoXe MaViTriNavigation { get; set; } = null!;
}
