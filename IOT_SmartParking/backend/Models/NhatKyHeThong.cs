using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SmartParking.Api.Models;

[Table("NhatKyHeThong")]
[Index("ThoiGianGhiNhan", Name = "IX_NhatKyHeThong_ThoiGian", AllDescending = true)]
public partial class NhatKyHeThong
{
    [Key]
    public long MaNhatKy { get; set; }

    public long? MaDon { get; set; }

    [StringLength(4)]
    [Unicode(false)]
    public string? MaViTri { get; set; }

    [StringLength(60)]
    [Unicode(false)]
    public string LoaiSuKien { get; set; } = string.Empty;

    [StringLength(20)]
    [Unicode(false)]
    public string NguonSuKien { get; set; } = "SYSTEM"; // ESP32, DASHBOARD, BACKEND, SYSTEM

    [StringLength(10)]
    [Unicode(false)]
    public string MucDo { get; set; } = "INFO"; // INFO, WARNING, ERROR

    [Column("MQTTTopic")]
    [StringLength(200)]
    [Unicode(false)]
    public string? Mqtttopic { get; set; }

    public string? NoiDung { get; set; }

    [Precision(0)]
    public DateTime ThoiGianGhiNhan { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(MaDon))]
    public virtual DonDatCho? DonDatCho { get; set; }
}
