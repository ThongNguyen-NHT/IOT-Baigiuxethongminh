using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Dtos;
using SmartParking.Api.Services;

namespace SmartParking.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly SmartParkingDbContext _db;
    private readonly JwtSettings _jwtSettings;

    public AuthController(
        SmartParkingDbContext db,
        IOptions<JwtSettings> jwtOptions)
    {
        _db = db;
        _jwtSettings = jwtOptions.Value;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Vui lòng nhập tên đăng nhập và mật khẩu." });
        }

        string username = request.Username.Trim();

        var user = await _db.TaiKhoans
            .FirstOrDefaultAsync(u => u.TenDangNhap == username);

        if (user == null || !PasswordSecurity.VerifyPassword(request.Password, user.MatKhauHash))
        {
            return Unauthorized(new { message = "Tên đăng nhập hoặc mật khẩu không chính xác." });
        }

        if (user.TrangThai != "ACTIVE")
        {
            return StatusCode(403, new { message = "Tài khoản của bạn đã bị khóa." });
        }

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.MaTaiKhoan.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.MaTaiKhoan.ToString()),
            new Claim(ClaimTypes.Name, user.TenDangNhap),
            new Claim(ClaimTypes.Role, user.VaiTro),
            new Claim("HoTen", user.HoTen)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpiryMinutes);

        var token = new JwtSecurityToken(
            _jwtSettings.Issuer,
            _jwtSettings.Audience,
            claims,
            expires: expires,
            signingCredentials: credentials);

        return Ok(new
        {
            accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt = expires,
            user = new
            {
                maTaiKhoan = user.MaTaiKhoan,
                tenDangNhap = user.TenDangNhap,
                hoTen = user.HoTen,
                vaiTro = user.VaiTro
            }
        });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out int userId))
        {
            return Unauthorized();
        }

        var user = await _db.TaiKhoans
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.MaTaiKhoan == userId);

        if (user == null)
        {
            return NotFound(new { message = "Không tìm thấy thông tin tài khoản." });
        }

        return Ok(new
        {
            maTaiKhoan = user.MaTaiKhoan,
            tenDangNhap = user.TenDangNhap,
            hoTen = user.HoTen,
            vaiTro = user.VaiTro,
            soDienThoai = user.SoDienThoai,
            email = user.Email
        });
    }
}
