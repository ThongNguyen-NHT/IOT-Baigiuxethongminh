using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartParking.Api.Data;
using SmartParking.Api.Models;

namespace SmartParking.Api.Controllers;

public class PhuongTienRequest
{
    public string BienSo { get; set; } = "";
    public string TenChuXe { get; set; } = "";
    public string? SoDienThoai { get; set; }
}

[ApiController]
[Route("api/phuong-tien")]
public class PhuongTienController : ControllerBase
{
    private readonly SmartParkingDbContext _db;

    public PhuongTienController(SmartParkingDbContext db)
    {
        _db = db;
    }

    // GET: api/phuong-tien
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var data = await _db.PhuongTiens
            .AsNoTracking()
            .OrderByDescending(x => x.MaPhuongTien)
            .Select(x => new
            {
                x.MaPhuongTien,
                x.BienSo,
                x.TenChuXe,
                x.SoDienThoai,
                x.NgayTao
            })
            .ToListAsync();

        return Ok(data);
    }

    // GET: api/phuong-tien/1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var data = await _db.PhuongTiens
            .AsNoTracking()
            .Where(x => x.MaPhuongTien == id)
            .Select(x => new
            {
                x.MaPhuongTien,
                x.BienSo,
                x.TenChuXe,
                x.SoDienThoai,
                x.NgayTao
            })
            .FirstOrDefaultAsync();

        if (data == null)
            return NotFound(new
            {
                message = "Không tìm thấy phương tiện."
            });

        return Ok(data);
    }

    // POST: api/phuong-tien
    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Create(
        [FromBody] PhuongTienRequest request)
    {
        string bienSo = (request.BienSo ?? "")
            .Trim().ToUpperInvariant();

        string tenChuXe = (request.TenChuXe ?? "").Trim();

        string? sdt = request.SoDienThoai?.Trim();

        if (bienSo.Length < 5 || bienSo.Length > 20 ||
            tenChuXe.Length == 0 || tenChuXe.Length > 100 ||
            sdt?.Length > 15)
        {
            return BadRequest(new
            {
                message = "Thông tin phương tiện không hợp lệ."
            });
        }

        bool tonTai = await _db.PhuongTiens
            .AnyAsync(x => x.BienSo == bienSo);

        if (tonTai)
            return Conflict(new
            {
                message = "Biển số xe đã tồn tại."
            });

        var xe = new PhuongTien
        {
            BienSo = bienSo,
            TenChuXe = tenChuXe,
            SoDienThoai = sdt,
            NgayTao = DateTime.Now
        };

        _db.PhuongTiens.Add(xe);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new
            {
                message = "Không thể thêm phương tiện. Hãy kiểm tra biển số."
            });
        }

        return CreatedAtAction(
            nameof(GetById),
            new { id = xe.MaPhuongTien },
            new
            {
                message = "Thêm phương tiện thành công.",
                xe.MaPhuongTien,
                xe.BienSo,
                xe.TenChuXe
            });
    }

    // PUT: api/phuong-tien/1
    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] PhuongTienRequest request)
    {
        var xe = await _db.PhuongTiens.FindAsync(id);

        if (xe == null)
            return NotFound(new
            {
                message = "Không tìm thấy phương tiện."
            });

        string bienSo = (request.BienSo ?? "")
            .Trim().ToUpperInvariant();

        string tenChuXe = (request.TenChuXe ?? "").Trim();

        string? sdt = request.SoDienThoai?.Trim();

        if (bienSo.Length < 5 || bienSo.Length > 20 ||
            tenChuXe.Length == 0 || tenChuXe.Length > 100 ||
            sdt?.Length > 15)
        {
            return BadRequest(new
            {
                message = "Dữ liệu không hợp lệ."
            });
        }

        bool trungBienSo = await _db.PhuongTiens
            .AnyAsync(x =>
                x.BienSo == bienSo &&
                x.MaPhuongTien != id);

        if (trungBienSo)
            return Conflict(new
            {
                message = "Biển số đã thuộc phương tiện khác."
            });

        xe.BienSo = bienSo;
        xe.TenChuXe = tenChuXe;
        xe.SoDienThoai = sdt;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new
            {
                message = "Cập nhật thất bại do xung đột dữ liệu."
            });
        }

        return Ok(new
        {
            message = "Cập nhật phương tiện thành công."
        });
    }

    // DELETE: api/phuong-tien/1
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var xe = await _db.PhuongTiens.FindAsync(id);

        if (xe == null)
            return NotFound();

        bool coLichSu = await _db.PhienGuiXes
            .AnyAsync(x => x.MaPhuongTien == id);

        if (coLichSu)
            return Conflict(new
            {
                message =
                    "Không thể xóa phương tiện đã có lịch sử gửi xe."
            });

        _db.PhuongTiens.Remove(xe);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new
            {
                message = "Không thể xóa vì dữ liệu đang được tham chiếu."
            });
        }

        return Ok(new
        {
            message = "Xóa phương tiện thành công."
        });
    }
}
