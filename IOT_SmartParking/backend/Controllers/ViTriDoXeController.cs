
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartParking.Api.Data;
using SmartParking.Api.Models;

namespace SmartParking.Api.Controllers;

public class CapNhatViTriRequest
{
    public string TrangThai { get; set; } = "";
    public decimal? KhoangCachCM { get; set; }
}

[ApiController]
[Route("api/vi-tri")]
public class ViTriDoXeController : ControllerBase
{
    private readonly SmartParkingDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly TimeProvider _timeProvider;

    public ViTriDoXeController(
        SmartParkingDbContext db,
        IWebHostEnvironment env,
        TimeProvider timeProvider)
    {
        _db = db;
        _env = env;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Lấy toàn bộ 40 ô đỗ xe kèm trạng thái thời gian thực (hỗ trợ BOOKED_FUTURE, HELD, GUIDING, thông tin đơn)
    /// </summary>
    [HttpGet("/api/slots")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSlotsWithRealtimeInfo([FromQuery] string? atTime = null)
    {
        DateTime targetTime = _timeProvider.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(atTime) && DateTime.TryParse(atTime, out var parsed))
        {
            targetTime = parsed.ToUniversalTime();
        }

        var slots = await _db.ViTriDoXes
            .AsNoTracking()
            .OrderBy(x => x.Hang)
            .ThenBy(x => x.Cot)
            .ToListAsync();

        var activeOrders = await _db.DonDatChoViTris
            .AsNoTracking()
            .Include(x => x.DonDatCho)
            .Where(x => x.DonDatCho.TrangThai != "EXPIRED" &&
                        x.DonDatCho.TrangThai != "NO_SHOW" &&
                        x.DonDatCho.TrangThai != "CANCELLED" &&
                        x.DonDatCho.TrangThai != "COMPLETED" &&
                        x.DonDatCho.TrangThai != "ABANDONED")
            .ToListAsync();

        var result = slots.Select(slot =>
        {
            var currentBooking = activeOrders
                .FirstOrDefault(o => o.MaViTri == slot.MaViTri &&
                                     o.DonDatCho.ThoiGianHenT <= targetTime &&
                                     o.DonDatCho.ThoiGianKetThucDuKien > targetTime);

            var futureBooking = activeOrders
                .FirstOrDefault(o => o.MaViTri == slot.MaViTri &&
                                     o.DonDatCho.ThoiGianHenT > targetTime);

            string effectiveStatus = slot.TrangThai;

            if (slot.TrangThai == "FREE" && futureBooking != null)
            {
                effectiveStatus = "BOOKED_FUTURE";
            }
            else if (currentBooking != null)
            {
                if (currentBooking.DonDatCho.TrangThai == "PENDING_PAYMENT")
                {
                    effectiveStatus = "HELD";
                }
                else if (currentBooking.DonDatCho.TrangThai == "PAID")
                {
                    effectiveStatus = "BOOKED";
                }
                else if (currentBooking.DonDatCho.TrangThai == "ENTERED")
                {
                    effectiveStatus = "GUIDING";
                }
                else if (currentBooking.DonDatCho.TrangThai == "PARKED")
                {
                    effectiveStatus = "OCCUPIED";
                }
            }

            var linkedBooking = currentBooking ?? futureBooking;

            return new
            {
                maViTri = slot.MaViTri,
                tenViTri = slot.TenViTri,
                hang = slot.Hang,
                cot = slot.Cot,
                laViTriAo = slot.LaViTriAo,
                trangThai = effectiveStatus,
                khoangCachCm = slot.KhoangCachCm,
                lanCapNhat = slot.LanCapNhat,
                bienSo = linkedBooking?.BienSo,
                maDon = linkedBooking?.MaDon,
                trangThaiDon = linkedBooking?.DonDatCho?.TrangThai,
                thoiGianHenT = linkedBooking?.DonDatCho?.ThoiGianHenT,
                thoiGianKetThuc = linkedBooking?.DonDatCho?.ThoiGianKetThucDuKien
            };
        });

        return Ok(result);
    }

    // GET: api/vi-tri
    // GET: api/vi-tri?trangThai=AVAILABLE
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? trangThai)
    {
        var query = _db.ViTriDoXes
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(trangThai))
        {
            string filter = trangThai.Trim().ToUpperInvariant();

            if (!new[] { "AVAILABLE", "RESERVED", "OCCUPIED" }
                .Contains(filter))
            {
                return BadRequest(new
                {
                    message = "Trạng thái lọc không hợp lệ."
                });
            }

            query = query.Where(x => x.TrangThai == filter);
        }

        var data = await query
            .OrderBy(x => x.MaViTri)
            .Select(x => new
            {
                x.MaViTri,
                x.TenViTri,
                x.TrangThai,
                x.KhoangCachCm,
                x.LanCapNhat
            })
            .ToListAsync();

        return Ok(data);
    }

    // GET: api/vi-tri/P01
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        id = id.ToUpperInvariant();

        var viTri = await _db.ViTriDoXes
            .AsNoTracking()
            .Where(x => x.MaViTri == id)
            .Select(x => new
            {
                x.MaViTri,
                x.TenViTri,
                x.TrangThai,
                x.KhoangCachCm,
                x.LanCapNhat
            })
            .FirstOrDefaultAsync();

        if (viTri == null)
            return NotFound(new
            {
                message = "Không tìm thấy vị trí đỗ."
            });

        return Ok(viTri);
    }

    // PUT: api/vi-tri/P01
    // Chi dung de thu nghiem trong Development
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] CapNhatViTriRequest request)
    {
        if (!_env.IsDevelopment())
            return StatusCode(403, new
            {
                message = "API cập nhật thử nghiệm đã bị khóa."
            });

        id = id.ToUpperInvariant();

        string trangThai = (request.TrangThai ?? "")
            .Trim().ToUpperInvariant();

        string[] hopLe =
        {
            "AVAILABLE", "RESERVED", "OCCUPIED"
        };

        if (!hopLe.Contains(trangThai))
            return BadRequest(new
            {
                message = "Trạng thái không hợp lệ."
            });

        if (request.KhoangCachCM < 0)
            return BadRequest(new
            {
                message = "Khoảng cách không được âm."
            });

        var viTri = await _db.ViTriDoXes.FindAsync(id);

        if (viTri == null)
            return NotFound();

        // Khong cho thu cong thay doi vi tri
        // dang duoc lien ket voi mot phien chua giai phong
        bool dangDuocGan = await _db.ChiTietViTriDos
            .AnyAsync(x =>
                x.MaViTri == id &&
                x.ThoiGianGiaiPhong == null);

        if (dangDuocGan)
            return Conflict(new
            {
                message =
                    "Vị trí đang thuộc phiên gửi xe. " +
                    "Không được cập nhật thủ công."
            });

        string trangThaiCu = viTri.TrangThai;
        var thoiGian = DateTime.Now;

        viTri.TrangThai = trangThai;
        viTri.KhoangCachCm = request.KhoangCachCM;
        viTri.LanCapNhat = thoiGian;

        if (trangThaiCu != trangThai)
        {
            _db.LichSuCamBiens.Add(new LichSuCamBien
            {
                MaViTri = id,
                KhoangCachCm = request.KhoangCachCM,
                TrangThaiCu = trangThaiCu,
                TrangThaiMoi = trangThai,
                ThoiGianGhiNhan = thoiGian
            });
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Cập nhật vị trí thành công.",
            maViTri = id,
            trangThaiCu,
            trangThaiMoi = trangThai
        });
    }
}
