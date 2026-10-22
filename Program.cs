using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using BilapConnect;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BilapDb>(o => o.UseSqlite(
    builder.Configuration.GetConnectionString("Bilap") ?? "Data Source=bilap.db"));
builder.Services.AddHttpClient<TwilioVerifyService>();
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("otp", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromMinutes(15);
        o.QueueLimit = 0;
    });
});
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Set Jwt__Key to a random secret of at least 32 bytes before running.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/chatHub"))
                context.Token = token;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BilapDb>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", app = "Bilap Connect" }));

app.MapPost("/api/auth/start", async (StartOtpRequest request, TwilioVerifyService sms) =>
{
    var phone = Phone.Normalize(request.Phone);
    if (phone is null) return Results.BadRequest(new { error = "أدخل رقم هاتف دولياً صحيحاً، مثال المغرب: +2126XXXXXXXX" });
    try
    {
        await sms.StartAsync(phone);
        return Results.Ok(new { message = "إذا كان الرقم صالحاً، فسيتم إرسال رمز التحقق." });
    }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 503); }
    catch { return Results.Problem("تعذر بدء التحقق. راجع إعدادات مزود الرسائل وحاول لاحقاً.", statusCode: 502); }
}).RequireRateLimiting("otp");

app.MapPost("/api/auth/verify", async (VerifyOtpRequest request, TwilioVerifyService sms, BilapDb db, IConfiguration config) =>
{
    var phone = Phone.Normalize(request.Phone);
    if (phone is null || string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 10)
        return Results.BadRequest(new { error = "رقم الهاتف أو الرمز غير صالح." });
    bool approved;
    try { approved = await sms.CheckAsync(phone, request.Code.Trim()); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 503); }
    catch { return Results.Problem("تعذر التحقق من الرمز.", statusCode: 502); }
    if (!approved) return Results.BadRequest(new { error = "الرمز غير صحيح أو انتهت صلاحيته." });

    var user = await db.Users.SingleOrDefaultAsync(u => u.Phone == phone);
    if (user is null)
    {
        user = new BilapUser { Phone = phone, DisplayName = "مستخدم Bilap", CreatedUtc = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("phone", user.Phone) };
    var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(12), signingCredentials: creds);
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token), user = new { user.Id, user.Phone, user.DisplayName } });
}).RequireRateLimiting("otp");

app.MapGet("/api/users", async (BilapDb db, ClaimsPrincipal principal) =>
{
    var myId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var users = await db.Users.Where(u => u.Id != myId)
        .OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName, u.Phone }).ToListAsync();
    return Results.Ok(users);
}).RequireAuthorization();

app.MapGet("/api/messages/{otherUserId:int}", async (int otherUserId, BilapDb db, ClaimsPrincipal principal) =>
{
    var myId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var messages = await db.Messages
        .Where(m => (m.SenderId == myId && m.RecipientId == otherUserId) ||
                    (m.SenderId == otherUserId && m.RecipientId == myId))
        .OrderBy(m => m.SentUtc).Take(300)
        .Select(m => new { m.Id, m.SenderId, m.RecipientId, m.Text, m.SentUtc }).ToListAsync();
    return Results.Ok(messages);
}).RequireAuthorization();

app.MapHub<ChatHub>("/chatHub").RequireAuthorization();
app.Run();
