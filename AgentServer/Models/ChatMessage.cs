namespace AgentServer.Models;

public class ChatMessage
{
    public int Id { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string? RoomId { get; set; }
    public string Message { get; set; } = string.Empty;
    public ChatMessageType Type { get; set; } = ChatMessageType.Room;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

public enum ChatMessageType
{
    Global,
    Room,
    Whisper
}
