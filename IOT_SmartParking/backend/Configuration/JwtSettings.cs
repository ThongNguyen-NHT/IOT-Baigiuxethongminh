namespace SmartParking.Api.Configuration;

public sealed class JwtSettings
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "SmartParking.Api";
    public string Audience { get; set; } = "SmartParking.Dashboard";
    public int ExpiryMinutes { get; set; } = 120;
}
