using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using SmartParking.Api.Configuration;
using SmartParking.Api.Data;
using SmartParking.Api.Hubs;
using SmartParking.Api.Services;

// 1. Cấu hình cơ bản
var builder = WebApplication.CreateBuilder(args);

// Cấu hình kết nối và khóa Demo
string connStr = builder.Configuration.GetConnectionString("SmartParkingConnection")
    ?? "Server=.\\SQLEXPRESS;Database=QL_BAIGIUXE_IOT;Trusted_Connection=True;TrustServerCertificate=True;";
string jwtKey = builder.Configuration["Jwt:Key"]
    ?? "SmartParkingDemoSecretKeyForCourseProject2026!";
string jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "SmartParking.Api";
string jwtAudience = builder.Configuration["Jwt:Audience"] ?? "SmartParking.Dashboard";

// 2. Controllers & SignalR
builder.Services.AddControllers();
builder.Services.AddSignalR();

// 3. TimeProvider voi TimeScale & Time Travel
builder.Services.AddSingleton<ScaledTimeProvider>();
builder.Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<ScaledTimeProvider>());

// 4. Configuration options
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<MqttSettings>(
    builder.Configuration.GetSection("MqttSettings"));
builder.Services.Configure<ParkingPolicySettings>(
    builder.Configuration.GetSection("ParkingPolicy"));

var jwtSettings = new JwtSettings
{
    Key = jwtKey,
    Issuer = jwtIssuer,
    Audience = jwtAudience,
    ExpiryMinutes = int.TryParse(builder.Configuration["Jwt:ExpiryMinutes"], out int exp) ? exp : 120
};

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// 5. Database DbContext
builder.Services.AddDbContext<SmartParkingDbContext>(options =>
{
    options.UseSqlServer(connStr);
});

// 6. Nghiep vu Services
builder.Services.AddScoped<ParkingOrderService>();
builder.Services.AddScoped<GateService>();
builder.Services.AddSingleton<IParkingNotificationService, ParkingNotificationService>();

// 7. Background Workers & MQTT Publisher
builder.Services.AddSingleton<ParkingTimeoutWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ParkingTimeoutWorker>());
builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBackgroundService>());
builder.Services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttBackgroundService>());

// 8. Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 9. CORS Policy linh hoat cho phep cau hinh
builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardPolicy", policy =>
    {
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Phục vụ trực tiếp giao diện Web từ thư mục dashboard/ (http://localhost:5137)
var dashboardDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "dashboard"));
if (!Directory.Exists(dashboardDir))
{
    dashboardDir = Path.Combine(app.Environment.ContentRootPath, "dashboard");
}

if (Directory.Exists(dashboardDir))
{
    var fileProvider = new PhysicalFileProvider(dashboardDir);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = ""
    });
}

app.UseCors("DashboardPolicy");

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ParkingHub>("/hub/parking").AllowAnonymous();
app.MapControllers();

app.Run();
