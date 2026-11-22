using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartParking.Api.Dtos;
using SmartParking.Api.Services;

namespace SmartParking.Api.Controllers;

[ApiController]
public class GateController : ControllerBase
{
    private readonly GateService _gateService;

    public GateController(GateService gateService)
    {
        _gateService = gateService;
    }

    private int GetCurrentUserId()
    {
        string? val = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(val, out int id))
        {
            return id;
        }
        throw new UnauthorizedAccessException("Không xác định được danh tính người dùng.");
    }

    /// <summary>
    /// Check-in bước 1: Quét mã EntryCode và nhận diện biển số ALPR tại cổng vào.
    /// Dành cho Nhân viên / Trạm điều khiển cổng (Staff, Admin).
    /// Khách hàng Customer không được tự gọi (trả về 403 Forbidden).
    /// </summary>
    [HttpPost("api/gate/in/scan")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> ScanGateIn([FromBody] GateInScanRequest request)
    {
        try
        {
            var result = await _gateService.ScanGateInAsync(request);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Check-in bước 2: Xe đã đi qua cổng vào (nhận sự kiện PASS từ cảm biến barie hoặc do Staff ghi nhận).
    /// Khách hàng KHÔNG được tự gọi endpoint này (sẽ trả về 403 Forbidden nếu gọi bằng tài khoản Customer).
    /// Trong chế độ Demo, sử dụng endpoint /api/demo/gate/pass (gated by DemoMode = true).
    /// </summary>
    [HttpPost("api/gate/in/pass")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> PassGateIn([FromBody] GateInPassRequest? request)
    {
        try
        {
            var result = await _gateService.ProcessGateInPassAsync(request?.OrderId);
            return Ok(new
            {
                message = "Xe đã qua barie cổng vào (ENTERED). Bắt đầu đếm thời gian vào ô đỗ.",
                booking = result
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Kiểm soát xe ra cổng:
    /// Dành cho Nhân viên / Trạm điều khiển cổng (Staff, Admin).
    /// Khách hàng Customer không được tự gọi (trả về 403 Forbidden).
    /// </summary>
    [HttpPost("api/gate/out/scan")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> ScanGateOut([FromBody] GateOutScanRequest request)
    {
        try
        {
            var result = await _gateService.ScanGateOutAsync(request);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Khách hàng bấm nút "Tôi đang kẹt" trên ứng dụng di động / PWA:
    /// Tự động gia hạn thêm ExtensionMin (5 phút) tìm ô. Khống chế tối đa 1 lần (MaxExtensions).
    /// </summary>
    [HttpPost("api/bookings/{id:long}/report-traffic-jam")]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> ReportTrafficJam(long id)
    {
        try
        {
            int userId = GetCurrentUserId();
            var result = await _gateService.ReportTrafficJamAsync(id, userId);
            return Ok(new
            {
                message = "Đã ghi nhận tình trạng kẹt xe và gia hạn thời gian tìm ô đỗ thành công.",
                booking = result
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Xe đi qua trạm kiểm soát Checkpoint nội bộ bãi xe:
    /// Tự động gia hạn thêm ExtensionMin thời gian tìm ô đỗ (mỗi checkpoint tối đa 1 lần).
    /// Khách hàng KHÔNG được tự gọi (trả về 403 Forbidden). Chỉ Staff/Admin hoặc tin nhắn MQTT từ cảm biến gọi.
    /// Trong chế độ Demo, sử dụng endpoint /api/demo/checkpoint/pass (gated by DemoMode = true).
    /// </summary>
    [HttpPost("api/checkpoint/{checkpointId}/pass")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> PassCheckpoint(string checkpointId, [FromBody] CheckpointPassRequest? request)
    {
        bool extended = await _gateService.PassCheckpointAsync(checkpointId, request?.OrderId);
        return Ok(new
        {
            success = extended,
            message = extended
                ? $"Đã ghi nhận xe qua checkpoint {checkpointId} và tự động gia hạn thời gian tìm ô."
                : $"Xe đã qua checkpoint {checkpointId} trước đó hoặc đã đạt giới hạn gia hạn tối đa."
        });
    }

    /// <summary>
    /// Danh sách mã vào cổng bị khóa (LOCKED)
    /// </summary>
    [HttpGet("api/gate/entry-code/locked")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> GetLockedCodes()
    {
        var list = await _gateService.GetLockedCodesAsync();
        return Ok(list);
    }

    /// <summary>
    /// Nhân viên mở khóa mã vào cổng (EntryCode) bị khóa do quét sai quá số lần quy định
    /// </summary>
    [HttpPost("api/gate/entry-code/{code}/unlock")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> UnlockEntryCode(string code)
    {
        try
        {
            string staffUsername = User.Identity?.Name ?? "Staff";
            await _gateService.UnlockEntryCodeAsync(code, staffUsername);
            return Ok(new
            {
                success = true,
                message = $"Đã mở khóa mã vào cổng {code} thành công và khôi phục số lần thử lại."
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Nhân viên xác nhận thu phí phạt quá giờ (OVERSTAY) tại cổng ra và mở cổng
    /// </summary>
    [HttpPost("api/gate/out/overstay/confirm")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> ConfirmOverstayPayment([FromBody] OverstayConfirmRequest request)
    {
        try
        {
            string staffUsername = User.Identity?.Name ?? "Staff";
            var result = await _gateService.ConfirmOverstayPaymentAsync(request, staffUsername);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Lấy lộ trình dẫn đường LED từ bảng DuongDanDen cho ô chỉ định và phát MQTT guide/cmd
    /// </summary>
    [HttpGet("api/gate/guide-path/{slotId}")]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> GetGuidePath(string slotId)
    {
        var result = await _gateService.GetGuidePathAsync(slotId);
        if (result == null)
        {
            return NotFound(new { message = $"Không tìm thấy lộ trình dẫn đường cho ô {slotId}." });
        }
        return Ok(result);
    }
}
