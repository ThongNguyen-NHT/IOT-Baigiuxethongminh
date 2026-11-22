using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SmartParking.Api.Models;

[Table("ChiTietViTriDo")]
[Index("MaPhien", Name = "IX_ChiTietViTriDo_MaPhien")]
[Index("MaPhien", "MaViTri", Name = "UQ_ChiTietViTriDo_Phien_ViTri", IsUnique = true)]
public partial class ChiTietViTriDo
{
    [Key]
    public long MaChiTiet { get; set; }

    public long MaPhien { get; set; }

    [StringLength(3)]
    [Unicode(false)]
    public string MaViTri { get; set; } = null!;

    [Precision(0)]
    public DateTime ThoiGianBatDau { get; set; }

    [Precision(0)]
    public DateTime? ThoiGianGiaiPhong { get; set; }

    [ForeignKey("MaPhien")]
    [InverseProperty("ChiTietViTriDos")]
    public virtual PhienGuiXe MaPhienNavigation { get; set; } = null!;

    [ForeignKey("MaViTri")]
    public virtual ViTriDoXe MaViTriNavigation { get; set; } = null!;
}
