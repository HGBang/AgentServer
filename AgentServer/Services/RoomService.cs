using AgentServer.Data;
using AgentServer.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentServer.Services;

public class RoomService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RoomService> _logger;

    public RoomService(IServiceProvider serviceProvider, ILogger<RoomService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<Room> CreateRoom(string hostUserId, string roomName, int maxPlayers, string mapName)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var room = new Room
        {
            RoomName = roomName,
            HostUserId = hostUserId,
            MaxPlayers = maxPlayers,
            MapName = mapName,
            State = RoomState.Waiting
        };

        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        _logger.LogInformation("Room created: {RoomId} by {HostUserId}", room.RoomId, hostUserId);
        return room;
    }

    public async Task<RoomParticipant?> JoinRoom(string roomId, string userId, string displayName)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var room = await db.Rooms
            .Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.RoomId == roomId);

        if (room == null || room.State == RoomState.Closed)
            return null;

        if (room.Participants.Count >= room.MaxPlayers)
            return null;

        if (room.Participants.Any(p => p.UserId == userId))
            return room.Participants.First(p => p.UserId == userId);

        var participant = new RoomParticipant
        {
            RoomId = room.Id,
            UserId = userId,
            DisplayName = displayName
        };

        db.RoomParticipants.Add(participant);
        await db.SaveChangesAsync();

        _logger.LogInformation("User {UserId} joined room {RoomId}", userId, roomId);
        return participant;
    }

    public async Task<bool> LeaveRoom(string roomId, string userId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var participant = await db.RoomParticipants
            .Include(p => p.Room)
            .FirstOrDefaultAsync(p => p.Room.RoomId == roomId && p.UserId == userId);

        if (participant == null)
            return false;

        db.RoomParticipants.Remove(participant);

        var room = await db.Rooms
            .Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.RoomId == roomId);

        // 호스트가 나가면 방 닫기
        if (room != null && room.HostUserId == userId)
        {
            room.State = RoomState.Closed;
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("User {UserId} left room {RoomId}", userId, roomId);
        return true;
    }

    public async Task<bool> SetReady(string roomId, string userId, bool isReady)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var participant = await db.RoomParticipants
            .Include(p => p.Room)
            .FirstOrDefaultAsync(p => p.Room.RoomId == roomId && p.UserId == userId);

        if (participant == null)
            return false;

        participant.IsReady = isReady;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Room?> GetRoom(string roomId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Rooms
            .Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.RoomId == roomId);
    }

    public async Task<List<Room>> GetRoomList()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Rooms
            .Include(r => r.Participants)
            .Where(r => r.State != RoomState.Closed)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task UpdateRoomState(string roomId, RoomState state, int? port = null, int? pid = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.RoomId == roomId);
        if (room != null)
        {
            room.State = state;
            if (port.HasValue) room.DedicatedServerPort = port;
            if (pid.HasValue) room.DedicatedServerPid = pid;
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetRoomUserIds(string roomId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.RoomParticipants
            .Include(p => p.Room)
            .Where(p => p.Room.RoomId == roomId)
            .Select(p => p.UserId)
            .ToListAsync();
    }
}
