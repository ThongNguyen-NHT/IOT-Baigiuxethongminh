using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SmartParking.Api.Models;

[Table("PhuongTien")]
[Index("BienSo", Name = "UQ_PhuongTien_BienSo", IsUnique = true)]
public partial class PhuongTien
{
    [Key]
    public int MaPhuongTien { get; set; }

    [StringLength(20)]
    [Unicode(false)]
    public string BienSo { get; set; } = null!;

    [StringLength(100)]
    public string TenChuXe { get; set; } = null!;

    [StringLength(15)]
    [Unicode(false)]
    public string? SoDienThoai { get; set; }

    [Precision(0)]
    public DateTime NgayTao { get; set; }

    [InverseProperty("MaPhuongTienNavigation")]
    public virtual ICollection<PhienGuiXe> PhienGuiXes { get; set; } = new List<PhienGuiXe>();
}
