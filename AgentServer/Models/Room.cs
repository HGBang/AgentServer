namespace AgentServer.Models;

public class Room
{
    public int Id { get; set; }
    public string RoomId { get; set; } = Guid.NewGuid().ToString("N");
    public string RoomName { get; set; } = string.Empty;
    public string HostUserId { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 16;
    public string MapName { get; set; } = string.Empty;
    public RoomState State { get; set; } = RoomState.Waiting;
    public int? DedicatedServerPort { get; set; }
    public int? DedicatedServerPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<RoomParticipant> Participants { get; set; } = new();
}

public enum RoomState
{
    Waiting,
    Starting,
    Running,
    Closed
}
