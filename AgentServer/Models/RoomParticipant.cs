namespace AgentServer.Models;

public class RoomParticipant
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
