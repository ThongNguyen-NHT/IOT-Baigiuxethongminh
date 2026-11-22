using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartParking.Api.Data;

namespace SmartParking.Api.Controllers;

[ApiController]
[Route("api/lich-su")]
public class LichSuController : ControllerBase
{
    private readonly SmartParkingDbContext _db;

    public LichSuController(SmartParkingDbContext db)
    {
        _db = db;
    }

    // GET: api/lich-su/cam-bien
    // GET: api/lich-su/cam-bien?maViTri=P01
    [HttpGet("cam-bien")]
    public async Task<IActionResult> GetCamBien(
        [FromQuery] string? maViTri,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = _db.LichSuCamBiens
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(maViTri))
        {
            string id = maViTri.Trim().ToUpperInvariant();
            query = query.Where(x => x.MaViTri == id);
        }

        var data = await query
            .OrderByDescending(x => x.ThoiGianGhiNhan)
            .Take(limit)
            .Select(x => new
            {
                x.MaLichSu,
                x.MaViTri,
                x.KhoangCachCm,
                x.TrangThaiCu,
                x.TrangThaiMoi,
                x.ThoiGianGhiNhan
            })
            .ToListAsync();

        return Ok(data);
    }

    // GET: api/lich-su/he-thong
    // Hỗ trợ lọc theo mức độ (INFO, WARNING, ERROR) và thời gian (tuNgay, denNgay)
    [HttpGet("he-thong")]
    public async Task<IActionResult> GetHeThong(
        [FromQuery] string? mucDo,
        [FromQuery] DateTime? tuNgay,
        [FromQuery] DateTime? denNgay,
        [FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);

        var query = _db.NhatKyHeThongs
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(mucDo) && mucDo.Trim().ToUpperInvariant() != "ALL")
        {
            string level = mucDo.Trim().ToUpperInvariant();
            query = query.Where(x => x.MucDo == level);
        }

        if (tuNgay.HasValue)
        {
            query = query.Where(x => x.ThoiGianGhiNhan >= tuNgay.Value.Date);
        }

        if (denNgay.HasValue)
        {
            DateTime denNgayEx = denNgay.Value.Date.AddDays(1);
            query = query.Where(x => x.ThoiGianGhiNhan < denNgayEx);
        }

        var data = await query
            .OrderByDescending(x => x.ThoiGianGhiNhan)
            .Take(limit)
            .Select(x => new
            {
                x.MaNhatKy,
                x.MaDon,
                x.MaViTri,
                x.LoaiSuKien,
                x.NguonSuKien,
                x.MucDo,
                x.Mqtttopic,
                x.NoiDung,
                x.ThoiGianGhiNhan
            })
            .ToListAsync();

        return Ok(data);
    }
}
