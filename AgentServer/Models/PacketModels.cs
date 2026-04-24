using System.Text.Json.Serialization;

namespace AgentServer.Models;

/// <summary>
/// Unreal Engine와 주고받는 JSON 패킷 구조
/// </summary>
public class Packet
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }
}

// ── 요청(Request) 모델 ──

public class LoginRequest
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class CreateRoomRequest
{
    public string RoomName { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 16;
    public string MapName { get; set; } = string.Empty;
}

public class JoinRoomRequest
{
    public string RoomId { get; set; } = string.Empty;
}

public class LeaveRoomRequest
{
    public string RoomId { get; set; } = string.Empty;
}

public class ChatRequest
{
    public string? RoomId { get; set; }
    public string Message { get; set; } = string.Empty;
    public ChatMessageType Type { get; set; } = ChatMessageType.Room;
    public string? TargetUserId { get; set; }
}

public class StartGameRequest
{
    public string RoomId { get; set; } = string.Empty;
}

public class ReadyRequest
{
    public string RoomId { get; set; } = string.Empty;
    public bool IsReady { get; set; }
}
