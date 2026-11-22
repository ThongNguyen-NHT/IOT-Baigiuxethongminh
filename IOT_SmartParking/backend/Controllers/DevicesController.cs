using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartParking.Api.Data;

namespace SmartParking.Api.Controllers;

[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly SmartParkingDbContext _db;

    public DevicesController(SmartParkingDbContext db)
    {
        _db = db;
    }

    // GET: api/devices
    [HttpGet]
    public async Task<IActionResult> GetDevices()
    {
        var devices = await _db.ThietBis
            .AsNoTracking()
            .OrderBy(d => d.MaThietBi)
            .Select(d => new
            {
                d.MaThietBi,
                d.Loai,
                d.HangO,
                d.PixelOffset,
                d.PixelCount,
                d.TrangThai,
                d.LanCapNhat,
                CacOPHuTrach = d.Loai == "GATE"
                    ? "Cổng vào, Cổng ra, LCD, 3 nút bấm (Cổng ra, CP1, CP2)"
                    : $"Hàng {d.HangO} ({d.HangO}1 - {d.HangO}8, 8 cảm biến siêu âm, 8 LED ô)"
            })
            .ToListAsync();

        return Ok(devices);
    }
}
