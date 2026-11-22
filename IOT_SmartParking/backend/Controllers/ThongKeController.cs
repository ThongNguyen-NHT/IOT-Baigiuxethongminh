using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartParking.Api.Data;
using System.Text;

namespace SmartParking.Api.Controllers;

[ApiController]
[Route("api/thong-ke")]
public class ThongKeController : ControllerBase
{
    private readonly SmartParkingDbContext _db;

    public ThongKeController(SmartParkingDbContext db)
    {
        _db = db;
    }

    // GET: api/thong-ke/tong-quan
    [HttpGet("tong-quan")]
    public async Task<IActionResult> TongQuan()
    {
        int tongViTri = await _db.ViTriDoXes.CountAsync();

        int soViTriTrong = await _db.ViTriDoXes
            .CountAsync(x => x.TrangThai == "FREE");

        int soViTriGiuCho = await _db.ViTriDoXes
            .CountAsync(x => x.TrangThai == "HELD" || x.TrangThai == "BOOKED" || x.TrangThai == "BOOKED_FUTURE" || x.TrangThai == "GUIDING");

        int soViTriCoXe = await _db.ViTriDoXes
            .CountAsync(x => x.TrangThai == "OCCUPIED" || x.TrangThai == "OVERLAP");

        int soPhienDangHoatDong = await _db.PhienGuiXes
            .CountAsync(x => x.TrangThai != "COMPLETED");

        int soPhienHoanTat = await _db.PhienGuiXes
            .CountAsync(x => x.TrangThai == "COMPLETED");

        decimal tongDoanhThu = await _db.PhienGuiXes
            .Where(x => x.TrangThai == "COMPLETED")
            .SumAsync(x => x.TongTien ?? 0m);

        return Ok(new
        {
            tongViTri,
            soViTriTrong,
            soViTriGiuCho,
            soViTriCoXe,
            soPhienDangHoatDong,
            soPhienHoanTat,
            tongDoanhThu
        });
    }

    // GET: api/thong-ke/doanh-thu-theo-ngay
    // GET: api/thong-ke/doanh-thu-theo-ngay?tuNgay=2026-10-01&denNgay=2026-10-31
    [HttpGet("doanh-thu-theo-ngay")]
    public async Task<IActionResult> DoanhThuTheoNgay(
        [FromQuery] DateTime? tuNgay,
        [FromQuery] DateTime? denNgay)
    {
        DateTime batDau = (tuNgay ?? DateTime.Today.AddDays(-6)).Date;
        DateTime ketThuc = (denNgay ?? DateTime.Today).Date;

        if (batDau > ketThuc)
        {
            return BadRequest(new
            {
                message = "Từ ngày không được lớn hơn đến ngày."
            });
        }

        DateTime ngayKetThucExclusive = ketThuc.AddDays(1);

        var rawData = await _db.PhienGuiXes
            .AsNoTracking()
            .Where(x =>
                x.TrangThai == "COMPLETED" &&
                x.ThoiGianRa >= batDau &&
                x.ThoiGianRa < ngayKetThucExclusive)
            .GroupBy(x => x.ThoiGianRa!.Value.Date)
            .Select(g => new
            {
                ngay = g.Key,
                soLuotGuiXe = g.Count(),
                doanhThu = g.Sum(x => x.TongTien ?? 0m)
            })
            .ToListAsync();

        // Đảm bảo đủ các ngày trong dải ngày để vẽ biểu đồ liền mạch
        var resultList = new List<object>();
        for (var d = batDau; d <= ketThuc; d = d.AddDays(1))
        {
            var match = rawData.FirstOrDefault(x => x.ngay == d);
            resultList.Add(new
            {
                ngay = d,
                ngayLabel = d.ToString("dd/MM"),
                soLuotGuiXe = match?.soLuotGuiXe ?? 0,
                doanhThu = match?.doanhThu ?? 0m
            });
        }

        decimal tongDoanhThu = rawData.Sum(x => x.doanhThu);
        int tongLuotGuiXe = rawData.Sum(x => x.soLuotGuiXe);

        return Ok(new
        {
            tuNgay = batDau,
            denNgay = ketThuc,
            tongLuotGuiXe,
            tongDoanhThu,
            chiTiet = resultList
        });
    }

    // GET: api/thong-ke/ty-le-lap-day
    [HttpGet("ty-le-lap-day")]
    public async Task<IActionResult> TyLeLapDay()
    {
        var slots = await _db.ViTriDoXes.AsNoTracking().ToListAsync();
        int tongSo = slots.Count;
        int coXe = slots.Count(s => s.TrangThai == "OCCUPIED" || s.TrangThai == "OVERLAP");
        int trong = slots.Count(s => s.TrangThai == "FREE");
        int daDat = slots.Count(s => s.TrangThai == "BOOKED" || s.TrangThai == "BOOKED_FUTURE" || s.TrangThai == "HELD" || s.TrangThai == "GUIDING");
        int khac = tongSo - coXe - trong - daDat;
        double tyLe = tongSo > 0 ? Math.Round((double)coXe / tongSo * 100.0, 1) : 0;

        return Ok(new
        {
            tongSo,
            coXe,
            trong,
            daDat,
            khac,
            tyLePhanTram = tyLe
        });
    }

    // GET: api/thong-ke/don-theo-trang-thai
    [HttpGet("don-theo-trang-thai")]
    public async Task<IActionResult> DonTheoTrangThai()
    {
        var raw = await _db.DonDatChos
            .AsNoTracking()
            .GroupBy(d => d.TrangThai)
            .Select(g => new
            {
                trangThai = g.Key,
                soLuong = g.Count()
            })
            .ToListAsync();

        return Ok(raw);
    }

    // GET: api/thong-ke/xuat-csv-doanh-thu
    [HttpGet("xuat-csv-doanh-thu")]
    public async Task<IActionResult> XuatCsvDoanhThu([FromQuery] DateTime? tuNgay, [FromQuery] DateTime? denNgay)
    {
        DateTime batDau = (tuNgay ?? DateTime.Today.AddDays(-30)).Date;
        DateTime ketThuc = (denNgay ?? DateTime.Today).Date.AddDays(1);

        var data = await _db.DonDatChos
            .AsNoTracking()
            .Include(d => d.DonDatChoViTris)
            .Where(x => x.ThoiGianDat >= batDau && x.ThoiGianDat < ketThuc)
            .OrderByDescending(x => x.ThoiGianDat)
            .ToListAsync();

        var sb = new StringBuilder();
        // UTF-8 BOM để Excel hiển thị tiếng Việt có dấu chính xác
        sb.Append('\uFEFF');
        sb.AppendLine("Mã đơn,Loại khách,Biển số,Vị trí,Thời gian đặt,Thời gian vào cổng,Thời gian ra cổng,Tổng tiền (VNĐ),Tiền phạt (VNĐ),Trạng thái");

        foreach (var r in data)
        {
            string bienSo = string.Join(";", r.DonDatChoViTris.Select(v => v.BienSo));
            string viTri = string.Join(";", r.DonDatChoViTris.Select(v => v.MaViTri));
            sb.AppendLine($"{r.MaDon},{r.LoaiKhach},\"{bienSo}\",\"{viTri}\",{r.ThoiGianDat:yyyy-MM-dd HH:mm},{r.ThoiGianVaoCong:yyyy-MM-dd HH:mm},{r.ThoiGianRaCong:yyyy-MM-dd HH:mm},{r.TongTien:F0},{r.TienPhat:F0},{r.TrangThai}");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv; charset=utf-8", $"doanh_thu_dondatcho_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    }
}
