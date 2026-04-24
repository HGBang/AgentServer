using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace AgentServer.Services;

/// <summary>
/// WebSocket 연결을 관리하는 싱글톤 서비스.
/// UserId <-> WebSocket 매핑.
/// </summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ConcurrentDictionary<string, string> _userRooms = new(); // userId -> roomId

    public bool AddConnection(string userId, WebSocket socket)
    {
        return _connections.TryAdd(userId, socket);
    }

    public bool RemoveConnection(string userId)
    {
        _userRooms.TryRemove(userId, out _);
        return _connections.TryRemove(userId, out _);
    }

    public WebSocket? GetConnection(string userId)
    {
        _connections.TryGetValue(userId, out var socket);
        return socket;
    }

    public IEnumerable<string> GetAllConnectedUserIds()
    {
        return _connections.Keys;
    }

    public void SetUserRoom(string userId, string roomId)
    {
        _userRooms[userId] = roomId;
    }

    public void RemoveUserRoom(string userId)
    {
        _userRooms.TryRemove(userId, out _);
    }

    public string? GetUserRoom(string userId)
    {
        _userRooms.TryGetValue(userId, out var roomId);
        return roomId;
    }

    public int ConnectionCount => _connections.Count;
}
