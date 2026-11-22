using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartParking.Api.Dtos;
using SmartParking.Api.Services;

namespace SmartParking.Api.Controllers;

[ApiController]
[Route("api/bookings")]
public class BookingController : ControllerBase
{
    private readonly ParkingOrderService _orderService;

    public BookingController(ParkingOrderService orderService)
    {
        _orderService = orderService;
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

    private string GetCurrentUserRole()
    {
        return User.FindFirstValue(ClaimTypes.Role) ?? "Customer";
    }

    /// <summary>
    /// Đặt chỗ giữ ô theo giờ (Customer, Staff, Admin)
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> CreateBooking([FromBody] CreateBookingRequest request)
    {
        try
        {
            int userId = GetCurrentUserId();
            var response = await _orderService.CreateBookingAsync(userId, request);
            return CreatedAtAction(nameof(GetById), new { id = response.MaDon }, response);
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
    /// Lấy thông tin chi tiết đơn đặt chỗ.
    /// Khách hàng Customer chỉ được xem đơn của chính mình; nếu truy cập đơn của người khác sẽ trả về 404 Not Found
    /// (tuân thủ OWASP API Security API1:2023 - BOLA để chống ID enumeration). Staff/Admin có quyền xem mọi đơn.
    /// </summary>
    [HttpGet("{id:long}")]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> GetById(long id)
    {
        int userId = GetCurrentUserId();
        string role = GetCurrentUserRole();

        var booking = await _orderService.GetBookingByIdAsync(id, userId, role);
        if (booking == null)
        {
            return NotFound(new { message = $"Không tìm thấy đơn hàng {id}." });
        }

        return Ok(booking);
    }

    /// <summary>
    /// Hủy đơn đặt chỗ khi đang PENDING_PAYMENT và giải phóng các ô đỗ HELD về FREE.
    /// Customer chỉ hủy được đơn của mình (đơn khác trả 404); Staff/Admin có quyền hủy mọi đơn.
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> CancelBooking(long id)
    {
        try
        {
            int userId = GetCurrentUserId();
            string role = GetCurrentUserRole();
            var response = await _orderService.CancelOrderAsync(id, userId, role);
            return Ok(new
            {
                message = "Đã hủy đơn đặt chỗ thành công và giải phóng các ô đỗ về trạng thái tự do (FREE).",
                booking = response
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
    /// Danh sách đơn đặt chỗ của khách hàng đang đăng nhập
    /// </summary>
    [HttpGet("my-bookings")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> GetMyBookings()
    {
        int userId = GetCurrentUserId();
        var bookings = await _orderService.GetCustomerBookingsAsync(userId);
        return Ok(bookings);
    }

    /// <summary>
    /// Danh sách tất cả đơn đặt chỗ dành cho Nhân viên (Staff) và Quản trị viên (Admin).
    /// Hỗ trợ lọc theo trạng thái (status: PENDING_PAYMENT, PAID, ENTERED, PARKED, OVERSTAY...)
    /// và phương thức thanh toán (paymentMethod: CASH, BANK_TRANSFER).
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> GetAllBookings([FromQuery] string? status, [FromQuery] string? paymentMethod, [FromQuery] int limit = 50)
    {
        var bookings = await _orderService.GetStaffBookingsAsync(status, paymentMethod, limit);
        return Ok(bookings);
    }

    /// <summary>
    /// Nhân viên xử lý sự cố khi khách vào bãi nhưng không còn ô trống hoặc sự cố ô đỗ
    /// - FREE_EXIT: Áp dụng chính sách NoSlotPolicy = FreeExit, cho xe ra miễn phí
    /// - REASSIGN: Chuyển sang ô trống mới khả dụng
    /// </summary>
    [HttpPost("{id:long}/resolve-no-slot")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> ResolveNoSlot(long id, [FromBody] ResolveNoSlotRequest request)
    {
        try
        {
            string? staffName = User.Identity?.Name ?? "Staff";
            var result = await _orderService.ResolveNoSlotAsync(id, request, staffName);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
