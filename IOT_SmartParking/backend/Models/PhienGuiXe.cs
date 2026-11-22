using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SmartParking.Api.Models;

[Table("PhienGuiXe")]
[Index("ThoiGianVao", Name = "IX_PhienGuiXe_ThoiGianVao", AllDescending = true)]
public partial class PhienGuiXe
{
    [Key]
    public long MaPhien { get; set; }

    public int MaPhuongTien { get; set; }

    [Column("MaPhienESP32")]
    [StringLength(80)]
    [Unicode(false)]
    public string? MaPhienEsp32 { get; set; }

    [StringLength(6)]
    [Unicode(false)]
    public string MaNhanXe { get; set; } = null!;

    [Precision(0)]
    public DateTime ThoiGianVao { get; set; }

    [Precision(0)]
    public DateTime? ThoiGianRa { get; set; }

    [StringLength(12)]
    [Unicode(false)]
    public string TrangThai { get; set; } = null!;

    [Column(TypeName = "decimal(18, 0)")]
    public decimal DonGiaMoiPhut { get; set; }

    public int? SoPhutTinhPhi { get; set; }

    [Column(TypeName = "decimal(18, 0)")]
    public decimal? TongTien { get; set; }

    [StringLength(500)]
    public string? GhiChu { get; set; }

    [InverseProperty("MaPhienNavigation")]
    public virtual ICollection<ChiTietViTriDo> ChiTietViTriDos { get; set; } = new List<ChiTietViTriDo>();

    [ForeignKey("MaPhuongTien")]
    [InverseProperty("PhienGuiXes")]
    public virtual PhuongTien MaPhuongTienNavigation { get; set; } = null!;
}
