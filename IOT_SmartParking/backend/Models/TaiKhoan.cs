using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartParking.Api.Models;

[Table("TaiKhoan")]
public class TaiKhoan
{
    [Key]
    public int MaTaiKhoan { get; set; }

    [Required]
    [MaxLength(50)]
    public string TenDangNhap { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string MatKhauHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string HoTen { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string VaiTro { get; set; } = "Customer"; // Customer, Staff, Admin

    [MaxLength(15)]
    public string? SoDienThoai { get; set; }

    [MaxLength(100)]
    public string? Email { get; set; }

    [Required]
    [MaxLength(20)]
    public string TrangThai { get; set; } = "ACTIVE"; // ACTIVE, LOCKED

    public DateTime NgayTao { get; set; } = DateTime.UtcNow;

    public virtual ICollection<DonDatCho> DonDatChos { get; set; } = new List<DonDatCho>();

    [InverseProperty(nameof(MaTienMat.Staff))]
    public virtual ICollection<MaTienMat> MaTienMatsDaCap { get; set; } = new List<MaTienMat>();

    [InverseProperty(nameof(MaTienMat.Customer))]
    public virtual ICollection<MaTienMat> MaTienMatsKhach { get; set; } = new List<MaTienMat>();
}
