namespace SmartParking.Api.Dtos;

public class CreateBookingRequest
{
    public DateTime ThoiGianHenT { get; set; }
    public int GoiThoiLuongGio { get; set; } = 1; // 1, 2, 4
    public string LoaiKhach { get; set; } = "DAT_TRUOC"; // DAT_TRUOC, VANG_LAI
    public List<BookingSlotItemDto> DanhSachViTri { get; set; } = new();
    public string? GhiChu { get; set; }
}

public class BookingSlotItemDto
{
    public string MaViTri { get; set; } = string.Empty;
    public string BienSo { get; set; } = string.Empty;
}

public class BookingResponseDto
{
    public long MaDon { get; set; }
    public int MaTaiKhoan { get; set; }
    public string LoaiKhach { get; set; } = string.Empty;
    public DateTime ThoiGianHenT { get; set; }
    public int GoiThoiLuongGio { get; set; }
    public DateTime ThoiGianKetThucDuKien { get; set; }
    public int SoLuongXe { get; set; }
    public decimal TongTien { get; set; }
    public string TrangThai { get; set; } = string.Empty;
    public DateTime? HanThanhToan { get; set; }
    public DateTime? HanCheckIn { get; set; }
    public DateTime? HanDoVaoO { get; set; }
    public int SoLanGiaHan { get; set; }
    public string? MaGiaoDichDuyNhat { get; set; }
    public List<string> DanhSachViTri { get; set; } = new();
    public List<EntryCodeDto> EntryCodes { get; set; } = new();
    public string? PhuongThucThanhToan { get; set; }
    public string? CashCode { get; set; }
    public string? BienSo { get; set; }
    public string? TenKhachHang { get; set; }
    public decimal TienPhat { get; set; }
    public DateTime? ThoiGianVaoCong { get; set; }
    public DateTime? ThoiGianRaCong { get; set; }
}

public class ResolveNoSlotRequest
{
    public string Action { get; set; } = "FREE_EXIT"; // FREE_EXIT, REASSIGN
    public string? NewSlotId { get; set; }
    public string? Reason { get; set; }
}

public class EntryCodeDto
{
    public string MaEntryCode { get; set; } = string.Empty;
    public string BienSo { get; set; } = string.Empty;
    public DateTime HanSuDung { get; set; }
    public string TrangThai { get; set; } = string.Empty;
}

public class BankTransferWebhookRequest
{
    public string MaGiaoDichDuyNhat { get; set; } = string.Empty;
    public decimal SoTien { get; set; }
    public string? NoiDungChuyenKhoan { get; set; }
}

public class CreateCashCodeRequest
{
    public long MaDon { get; set; }
}

public class RedeemCashCodeRequest
{
    public long MaDon { get; set; }
    public string MaCashCode { get; set; } = string.Empty;
}

public class GateInScanRequest
{
    public string EntryCode { get; set; } = string.Empty;
    public string? BienSo { get; set; }
    public string? ImageBase64 { get; set; }
    public string? ImageFileName { get; set; }
}

public class GateInPassRequest
{
    public long? OrderId { get; set; }
}

public class GateOutScanRequest
{
    public string? BienSo { get; set; }
    public long? OrderId { get; set; }
}

public class GateScanResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long? OrderId { get; set; }
    public string? SlotId { get; set; }
    public int GateTransitSec { get; set; } = 60;
    public string Status { get; set; } = string.Empty;
    public string? RecognizedPlate { get; set; }
    public bool RequireStaffPayment { get; set; } = false;
    public decimal PenaltyFee { get; set; } = 0m;
    public int PenaltyMinutes { get; set; } = 0;
}

public class CheckpointPassRequest
{
    public string CheckpointId { get; set; } = string.Empty;
    public long? OrderId { get; set; }
}

public class OverstayConfirmRequest
{
    public long OrderId { get; set; }
    public decimal? AmountPaid { get; set; }
    public string? StaffNote { get; set; }
}

public class UnlockEntryCodeRequest
{
    public string EntryCode { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
