using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AgentServer.Services;

/// <summary>
/// 특정 유저/방/전체에 메시지를 전송하는 유틸리티 서비스.
/// </summary>
public class MessageBroadcaster
{
    private readonly ConnectionManager _connections;
    private readonly RoomService _roomService;
    private readonly ILogger<MessageBroadcaster> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MessageBroadcaster(ConnectionManager connections, RoomService roomService,
        ILogger<MessageBroadcaster> logger)
    {
        _connections = connections;
        _roomService = roomService;
        _logger = logger;
    }

    public async Task SendToUser(string userId, object message)
    {
        var socket = _connections.GetConnection(userId);
        if (socket is { State: WebSocketState.Open })
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    public async Task SendToRoom(string roomId, object message, string? excludeUserId = null)
    {
        var userIds = await _roomService.GetRoomUserIds(roomId);
        var tasks = userIds
            .Where(uid => uid != excludeUserId)
            .Select(uid => SendToUser(uid, message));
        await Task.WhenAll(tasks);
    }

    public async Task SendToAll(object message, string? excludeUserId = null)
    {
        var tasks = _connections.GetAllConnectedUserIds()
            .Where(uid => uid != excludeUserId)
            .Select(uid => SendToUser(uid, message));
        await Task.WhenAll(tasks);
    }
}
