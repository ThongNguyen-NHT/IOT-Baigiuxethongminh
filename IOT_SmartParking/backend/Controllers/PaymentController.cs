using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartParking.Api.Dtos;
using SmartParking.Api.Services;

namespace SmartParking.Api.Controllers;

[ApiController]
public class PaymentController : ControllerBase
{
    private readonly ParkingOrderService _orderService;

    public PaymentController(ParkingOrderService orderService)
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
    /// Webhook ngân hàng thật: Bắt buộc chữ ký HMAC-SHA256 (X-Signature) và timestamp chống replay (X-Timestamp).
    /// </summary>
    [HttpPost("api/payment/webhook/bank-transfer")]
    [AllowAnonymous]
    public async Task<IActionResult> BankTransferWebhook(
        [FromBody] BankTransferWebhookRequest request,
        [FromHeader(Name = "X-Signature")] string? signature,
        [FromHeader(Name = "X-Timestamp")] string? timestamp)
    {
        try
        {
            var result = await _orderService.ConfirmBankTransferWithHmacAsync(request, signature, timestamp);
            return Ok(new
            {
                message = "Xác nhận chuyển khoản ngân hàng qua Webhook thành công.",
                booking = result
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(401, new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Endpoint mô phỏng thanh toán thành công dành riêng cho Demo (chỉ khả dụng khi DemoMode = true).
    /// Yêu cầu JWT token; Customer chỉ được mô phỏng đơn của chính mình.
    /// Nếu DemoMode = false hoặc Customer gọi đơn của người khác -> trả về 404 Not Found.
    /// </summary>
    [HttpPost("api/demo/payment/{maDon:long}/simulate-success")]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> SimulateDemoPayment(long maDon)
    {
        try
        {
            int userId = GetCurrentUserId();
            string role = GetCurrentUserRole();
            var result = await _orderService.SimulateDemoPaymentAsync(maDon, userId, role);
            return Ok(new
            {
                message = "Mô phỏng thanh toán thành công (DemoMode).",
                booking = result
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Nhân viên Staff xác nhận đã thu tiền mặt tại quầy và phát hành mã CashCode (Chỉ Staff và Admin).
    /// </summary>
    [HttpPost("api/payment/cash/confirm")]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> StaffConfirmCash([FromBody] CreateCashCodeRequest request)
    {
        try
        {
            int staffId = GetCurrentUserId();
            string cashCode = await _orderService.StaffCreateCashCodeAsync(request.MaDon, staffId);
            return Ok(new
            {
                message = "Đã tạo mã xác thực tiền mặt (CashCode) thành công.",
                maDon = request.MaDon,
                cashCode = cashCode,
                hanSuDungPhut = 30
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Khách hàng nhập mã CashCode để kích hoạt đơn PAID.
    /// </summary>
    [HttpPost("api/payment/cash/redeem")]
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> CustomerRedeemCash([FromBody] RedeemCashCodeRequest request)
    {
        try
        {
            int userId = GetCurrentUserId();
            string role = GetCurrentUserRole();
            var result = await _orderService.RedeemCashCodeAsync(request, userId, role);
            return Ok(new
            {
                message = "Quy đổi mã tiền mặt thành công. Đơn hàng đã được thanh toán (PAID).",
                booking = result
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
