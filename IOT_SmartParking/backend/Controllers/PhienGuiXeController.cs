
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using SmartParking.Api.Data;
using SmartParking.Api.Models;

namespace SmartParking.Api.Controllers;

public class TaoPhienRequest
{
    public int MaPhuongTien { get; set; }
    public string MaViTri { get; set; } = "";
    public string MaNhanXe { get; set; } = "";
    public string? MaPhienESP32 { get; set; }
}

[ApiController]
[Route("api/phien-gui-xe")]
public class PhienGuiXeController : ControllerBase
{
    private readonly SmartParkingDbContext _db;
    private readonly IWebHostEnvironment _env;

    public PhienGuiXeController(
        SmartParkingDbContext db,
        IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    // GET: api/phien-gui-xe
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? trangThai)
    {
        var query = _db.PhienGuiXes
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(trangThai))
        {
            string filter = trangThai.Trim().ToUpperInvariant();

            if (!new[]
                {
                    "PARKING", "PARKED",
                    "EXITING", "COMPLETED"
                }.Contains(filter))
            {
                return BadRequest(new
                {
                    message = "Trạng thái không hợp lệ."
                });
            }

            query = query.Where(x => x.TrangThai == filter);
        }

        var data = await query
            .OrderByDescending(x => x.ThoiGianVao)
            .Select(x => new
            {
                x.MaPhien,
                x.MaPhuongTien,
                x.MaPhienEsp32,
                BienSo = x.MaPhuongTienNavigation.BienSo,
                TenChuXe = x.MaPhuongTienNavigation.TenChuXe,
                x.ThoiGianVao,
                x.ThoiGianRa,
                x.TrangThai,
                x.DonGiaMoiPhut,
                x.SoPhutTinhPhi,
                x.TongTien,
                ViTriDos = x.ChiTietViTriDos
                    .OrderBy(ct => ct.MaViTri)
                    .Select(ct => new
                    {
                        ct.MaViTri,
                        ct.ThoiGianBatDau,
                        ct.ThoiGianGiaiPhong
                    })
                    .ToList()
            })
            .ToListAsync();

        return Ok(data);
    }

    // GET: api/phien-gui-xe/1
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var phien = await _db.PhienGuiXes
            .AsNoTracking()
            .Where(x => x.MaPhien == id)
            .Select(x => new
            {
                x.MaPhien,
                x.MaPhuongTien,
                x.MaPhienEsp32,
                x.ThoiGianVao,
                x.ThoiGianRa,
                x.TrangThai,
                x.DonGiaMoiPhut,
                x.SoPhutTinhPhi,
                x.TongTien
            })
            .FirstOrDefaultAsync();

        if (phien == null)
            return NotFound(new
            {
                message = "Không tìm thấy phiên gửi xe."
            });

        var viTri = await _db.ChiTietViTriDos
            .AsNoTracking()
            .Where(x => x.MaPhien == id)
            .OrderBy(x => x.MaViTri)
            .Select(x => new
            {
                x.MaViTri,
                x.ThoiGianBatDau,
                x.ThoiGianGiaiPhong
            })
            .ToListAsync();

        return Ok(new { phien, viTri });
    }

    // POST: api/phien-gui-xe
    // Chi tao phien thu nghiem trong Development
    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Create(
        [FromBody] TaoPhienRequest request)
    {
        if (!_env.IsDevelopment())
            return StatusCode(403, new
            {
                message = "API tạo phiên thử nghiệm đã bị khóa."
            });

        string maViTri = (request.MaViTri ?? "")
            .Trim().ToUpperInvariant();

        string maNhanXe = (request.MaNhanXe ?? "").Trim();

        if (maNhanXe.Length != 6 ||
            !maNhanXe.All(c => c >= '0' && c <= '9'))
        {
            return BadRequest(new
            {
                message = "Mã nhận xe phải gồm đúng 6 chữ số."
            });
        }

        if (request.MaPhienESP32?.Length > 80)
            return BadRequest(new
            {
                message = "Mã phiên ESP32 quá dài."
            });

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable);

        var xe = await _db.PhuongTiens
            .FindAsync(request.MaPhuongTien);

        if (xe == null)
            return BadRequest(new
            {
                message = "Phương tiện không tồn tại."
            });

        bool xeDangGui = await _db.PhienGuiXes
            .AnyAsync(x =>
                x.MaPhuongTien == request.MaPhuongTien &&
                x.TrangThai != "COMPLETED");

        if (xeDangGui)
            return Conflict(new
            {
                message = "Phương tiện đang có phiên hoạt động."
            });

        var viTri = await _db.ViTriDoXes
            .FindAsync(maViTri);

        if (viTri == null ||
            viTri.TrangThai != "AVAILABLE")
        {
            return Conflict(new
            {
                message = "Vị trí không khả dụng."
            });
        }

        bool daDuocGan = await _db.ChiTietViTriDos
            .AnyAsync(x =>
                x.MaViTri == maViTri &&
                x.ThoiGianGiaiPhong == null);

        if (daDuocGan)
            return Conflict(new
            {
                message = "Vị trí đang thuộc phiên khác."
            });

        if (!string.IsNullOrWhiteSpace(request.MaPhienESP32))
        {
            bool trungPhien = await _db.PhienGuiXes
                .AnyAsync(x =>
                    x.MaPhienEsp32 == request.MaPhienESP32);

            if (trungPhien)
                return Conflict(new
                {
                    message = "Mã phiên ESP32 đã tồn tại."
                });
        }

        var now = DateTime.Now;

        var phien = new PhienGuiXe
        {
            MaPhuongTien = request.MaPhuongTien,
            MaPhienEsp32 = request.MaPhienESP32,
            MaNhanXe = maNhanXe,
            ThoiGianVao = now,
            TrangThai = "PARKING",
            DonGiaMoiPhut = 2000
        };

        _db.PhienGuiXes.Add(phien);
        await _db.SaveChangesAsync();

        _db.ChiTietViTriDos.Add(new ChiTietViTriDo
        {
            MaPhien = phien.MaPhien,
            MaViTri = maViTri,
            ThoiGianBatDau = now
        });

        viTri.TrangThai = "RESERVED";
        viTri.LanCapNhat = now;

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return CreatedAtAction(
            nameof(GetById),
            new { id = phien.MaPhien },
            new
            {
                message = "Tạo phiên thử nghiệm thành công.",
                phien.MaPhien,
                phien.TrangThai,
                maViTri
            });
    }

    // PUT: api/phien-gui-xe/1/hoan-tat
    // Chi hoan tat thu nghiem trong Development
    [HttpPut("{id:long}/hoan-tat")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Complete(long id)
    {
        if (!_env.IsDevelopment())
            return StatusCode(403, new
            {
                message = "API hoàn tất thử nghiệm đã bị khóa."
            });

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable);

        var phien = await _db.PhienGuiXes.FindAsync(id);

        if (phien == null)
            return NotFound();

        if (phien.TrangThai == "COMPLETED")
            return Conflict(new
            {
                message = "Phiên đã hoàn tất."
            });

        var chiTiet = await _db.ChiTietViTriDos
            .Where(x =>
                x.MaPhien == id &&
                x.ThoiGianGiaiPhong == null)
            .ToListAsync();

        var danhSachMaViTri = chiTiet
            .Select(x => x.MaViTri)
            .ToList();

        var danhSachViTri = await _db.ViTriDoXes
            .Where(x => danhSachMaViTri.Contains(x.MaViTri))
            .ToListAsync();

        if (danhSachViTri.Any(
            x => x.TrangThai == "OCCUPIED"))
        {
            return Conflict(new
            {
                message = "Xe vẫn còn chiếm vị trí đỗ."
            });
        }

        var now = DateTime.Now;

        int soPhut = Math.Max(
            1,
            (int)Math.Ceiling(
                (now - phien.ThoiGianVao).TotalMinutes)
        );

        phien.ThoiGianRa = now;
        phien.SoPhutTinhPhi = soPhut;
        phien.TongTien =
            soPhut * phien.DonGiaMoiPhut;

        phien.TrangThai = "COMPLETED";

        foreach (var ct in chiTiet)
            ct.ThoiGianGiaiPhong = now;

        foreach (var viTri in danhSachViTri)
        {
            viTri.TrangThai = "AVAILABLE";
            viTri.LanCapNhat = now;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new
        {
            message = "Hoàn tất phiên thử nghiệm thành công.",
            phien.MaPhien,
            phien.SoPhutTinhPhi,
            phien.TongTien
        });
    }
}
