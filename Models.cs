using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BilapConnect;

public record StartOtpRequest(string Phone);
public record VerifyOtpRequest(string Phone, string Code);

public class BilapUser
{
    public int Id { get; set; }
    public string Phone { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
public class ChatMessage
{
    public int Id { get; set; }
    public int SenderId { get; set; }
    public int RecipientId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; }
}
public class BilapDb(DbContextOptions<BilapDb> options) : DbContext(options)
{
    public DbSet<BilapUser> Users => Set<BilapUser>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<BilapUser>().HasIndex(u => u.Phone).IsUnique();
        model.Entity<ChatMessage>().Property(m => m.Text).HasMaxLength(2000);
    }
}

public class ChatHub(BilapDb db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var id = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is not null) await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{id}");
        await base.OnConnectedAsync();
    }

    public async Task SendMessage(int recipientId, string text)
    {
        var senderClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(senderClaim, out var senderId)) throw new HubException("يلزم تسجيل الدخول.");
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 2000) throw new HubException("الرسالة فارغة أو طويلة جداً.");
        if (senderId == recipientId) throw new HubException("لا يمكن إرسال رسالة إلى نفسك.");
        var recipientExists = await db.Users.AnyAsync(u => u.Id == recipientId);
        if (!recipientExists) throw new HubException("المستخدم غير موجود.");
        var message = new ChatMessage { SenderId = senderId, RecipientId = recipientId, Text = text, SentUtc = DateTime.UtcNow };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        var payload = new { message.Id, message.SenderId, message.RecipientId, message.Text, message.SentUtc };
        await Clients.Group($"user:{recipientId}").SendAsync("ReceiveMessage", payload);
        await Clients.Caller.SendAsync("ReceiveMessage", payload);
    }
}
