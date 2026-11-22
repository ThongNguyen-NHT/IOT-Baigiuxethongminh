using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("MaTienMat")]
public class MaTienMat
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long MaDon { get; set; }

    [Required]
    [MaxLength(64)]
    public string MaCashCodeHash { get; set; } = string.Empty;

    public int MaStaff { get; set; }

    public int MaTaiKhoanCustomer { get; set; }

    public DateTime HanSuDung { get; set; }

    public bool DaSuDung { get; set; } = false;

    public int SoLanNhapSai { get; set; } = 0;

    public bool BiKhoa { get; set; } = false;

    public DateTime? ThoiGianSuDung { get; set; }

    public DateTime NgayTao { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(MaDon))]
    public virtual DonDatCho DonDatCho { get; set; } = null!;

    [ForeignKey(nameof(MaStaff))]
    [InverseProperty(nameof(TaiKhoan.MaTienMatsDaCap))]
    public virtual TaiKhoan Staff { get; set; } = null!;

    [ForeignKey(nameof(MaTaiKhoanCustomer))]
    [InverseProperty(nameof(TaiKhoan.MaTienMatsKhach))]
    public virtual TaiKhoan Customer { get; set; } = null!;
}
