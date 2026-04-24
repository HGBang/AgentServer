using AgentServer.Data;
using AgentServer.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentServer.Services;

public class ChatService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChatService> _logger;

    public ChatService(IServiceProvider serviceProvider, ILogger<ChatService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ChatMessage> SaveMessage(string senderId, string senderName, string message,
        ChatMessageType type, string? roomId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var chatMessage = new ChatMessage
        {
            SenderId = senderId,
            SenderName = senderName,
            Message = message,
            Type = type,
            RoomId = roomId,
            SentAt = DateTime.UtcNow
        };

        db.ChatMessages.Add(chatMessage);
        await db.SaveChangesAsync();

        _logger.LogInformation("Chat [{Type}] {SenderId}: {Message}", type, senderId, message);
        return chatMessage;
    }

    public async Task<List<ChatMessage>> GetRoomHistory(string roomId, int count = 50)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ChatMessages
            .Where(m => m.RoomId == roomId)
            .OrderByDescending(m => m.SentAt)
            .Take(count)
            .OrderBy(m => m.SentAt)
            .ToListAsync();
    }

    public async Task<List<ChatMessage>> GetGlobalHistory(int count = 50)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ChatMessages
            .Where(m => m.Type == ChatMessageType.Global)
            .OrderByDescending(m => m.SentAt)
            .Take(count)
            .OrderBy(m => m.SentAt)
            .ToListAsync();
    }
}
