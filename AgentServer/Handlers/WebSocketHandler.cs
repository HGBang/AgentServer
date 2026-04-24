using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentServer.Models;
using AgentServer.Services;

namespace AgentServer.Handlers;

/// <summary>
/// WebSocket 메시지 수신 및 패킷 타입별 핸들링.
/// Unreal Engine 클라이언트와의 통신 프로토콜 처리.
/// </summary>
public class WebSocketHandler
{
    private readonly ConnectionManager _connections;
    private readonly UserService _userService;
    private readonly RoomService _roomService;
    private readonly ChatService _chatService;
    private readonly DedicatedServerLauncher _dsLauncher;
    private readonly MessageBroadcaster _broadcaster;
    private readonly GameLogger _gameLogger;
    private readonly ILogger<WebSocketHandler> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public WebSocketHandler(
        ConnectionManager connections,
        UserService userService,
        RoomService roomService,
        ChatService chatService,
        DedicatedServerLauncher dsLauncher,
        MessageBroadcaster broadcaster,
        GameLogger gameLogger,
        ILogger<WebSocketHandler> logger)
    {
        _connections = connections;
        _userService = userService;
        _roomService = roomService;
        _chatService = chatService;
        _dsLauncher = dsLauncher;
        _broadcaster = broadcaster;
        _gameLogger = gameLogger;
        _logger = logger;
    }

    public async Task HandleConnection(WebSocket webSocket)
    {
        string? currentUserId = null;
        var buffer = new byte[1024 * 8];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogDebug("Received: {Json}", json);

                var packet = JsonSerializer.Deserialize<Packet>(json, JsonOptions);
                if (packet == null) continue;

                currentUserId = await ProcessPacket(packet, webSocket, currentUserId);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("WebSocket error: {Message}", ex.Message);
        }
        finally
        {
            if (currentUserId != null)
            {
                await HandleDisconnect(currentUserId);
            }

            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
            }
        }
    }

    private async Task<string?> ProcessPacket(Packet packet, WebSocket webSocket, string? currentUserId)
    {
        var dataJson = packet.Data != null ? JsonSerializer.Serialize(packet.Data) : "{}";

        switch (packet.Type)
        {
            case "login":
            {
                var req = JsonSerializer.Deserialize<LoginRequest>(dataJson, JsonOptions)!;
                var user = await _userService.LoginOrRegister(req.UserId, req.DisplayName);

                // 기존 연결이 있으면 교체
                _connections.RemoveConnection(req.UserId);
                _connections.AddConnection(req.UserId, webSocket);
                currentUserId = req.UserId;

                // 여유 있는 로비 DS에 배정
                var lobby = _dsLauncher.AssignUserToLobby(req.UserId);

                if (lobby != null)
                {
                    await _broadcaster.SendToUser(req.UserId, new
                    {
                        type = "login_result",
                        success = true,
                        userId = user.UserId,
                        displayName = user.DisplayName,
                        lobbyServerIp = _dsLauncher.LobbyIp,
                        lobbyServerPort = lobby.Port,
                        lobbyId = lobby.LobbyId
                    });
                }
                else
                {
                    await _broadcaster.SendToUser(req.UserId, new
                    {
                        type = "login_result",
                        success = false,
                        error = "No lobby server available"
                    });
                }

                _logger.LogInformation("User logged in: {UserId}", req.UserId);
                await _gameLogger.LogRoomEvent("LOGIN", "SYSTEM", req.UserId, req.DisplayName);
                break;
            }

            case "create_room":
            {
                if (currentUserId == null) break;
                var req = JsonSerializer.Deserialize<CreateRoomRequest>(dataJson, JsonOptions)!;
                var room = await _roomService.CreateRoom(currentUserId, req.RoomName, req.MaxPlayers, req.MapName);

                // 호스트 자동 참가
                var user = await _userService.GetUser(currentUserId);
                await _roomService.JoinRoom(room.RoomId, currentUserId, user?.DisplayName ?? currentUserId);
                _connections.SetUserRoom(currentUserId, room.RoomId);

                await _broadcaster.SendToUser(currentUserId, new
                {
                    type = "room_created",
                    roomId = room.RoomId,
                    roomName = room.RoomName,
                    hostUserId = room.HostUserId,
                    maxPlayers = room.MaxPlayers,
                    mapName = room.MapName
                });

                // 전체에 방 목록 갱신 알림
                await BroadcastRoomList();
                await _gameLogger.LogRoomEvent("CREATED", room.RoomId, currentUserId,
                    user?.DisplayName ?? currentUserId,
                    $"RoomName:{req.RoomName} MaxPlayers:{req.MaxPlayers} Map:{req.MapName}");
                break;
            }

            case "join_room":
            {
                if (currentUserId == null) break;
                var req = JsonSerializer.Deserialize<JoinRoomRequest>(dataJson, JsonOptions)!;
                var user = await _userService.GetUser(currentUserId);
                var participant = await _roomService.JoinRoom(req.RoomId, currentUserId,
                    user?.DisplayName ?? currentUserId);

                if (participant != null)
                {
                    _connections.SetUserRoom(currentUserId, req.RoomId);

                    await _broadcaster.SendToUser(currentUserId, new
                    {
                        type = "join_room_result",
                        success = true,
                        roomId = req.RoomId
                    });

                    // 방 참가자 갱신 알림
                    await BroadcastRoomUpdate(req.RoomId);
                    await _gameLogger.LogRoomEvent("JOIN", req.RoomId, currentUserId,
                        user?.DisplayName ?? currentUserId);

                    // 채팅 히스토리 전송
                    var history = await _chatService.GetRoomHistory(req.RoomId);
                    await _broadcaster.SendToUser(currentUserId, new
                    {
                        type = "chat_history",
                        roomId = req.RoomId,
                        messages = history.Select(m => new
                        {
                            senderId = m.SenderId,
                            senderName = m.SenderName,
                            message = m.Message,
                            sentAt = m.SentAt
                        })
                    });
                }
                else
                {
                    await _broadcaster.SendToUser(currentUserId, new
                    {
                        type = "join_room_result",
                        success = false,
                        error = "Cannot join room (full or closed)"
                    });
                }
                break;
            }

            case "leave_room":
            {
                if (currentUserId == null) break;
                var req = JsonSerializer.Deserialize<LeaveRoomRequest>(dataJson, JsonOptions)!;
                var leaveUser = await _userService.GetUser(currentUserId);
                await _roomService.LeaveRoom(req.RoomId, currentUserId);
                _connections.RemoveUserRoom(currentUserId);

                await _broadcaster.SendToUser(currentUserId, new
                {
                    type = "leave_room_result",
                    success = true
                });

                await BroadcastRoomUpdate(req.RoomId);
                await BroadcastRoomList();
                await _gameLogger.LogRoomEvent("LEAVE", req.RoomId, currentUserId,
                    leaveUser?.DisplayName ?? currentUserId);
                break;
            }

            case "chat":
            {
                if (currentUserId == null) break;
                var req = JsonSerializer.Deserialize<ChatRequest>(dataJson, JsonOptions)!;
                var sender = await _userService.GetUser(currentUserId);
                var senderName = sender?.DisplayName ?? currentUserId;

                var msg = await _chatService.SaveMessage(
                    currentUserId, senderName, req.Message, req.Type, req.RoomId);

                var chatPayload = new
                {
                    type = "chat_message",
                    senderId = currentUserId,
                    senderName,
                    message = req.Message,
                    chatType = req.Type.ToString().ToLower(),
                    roomId = req.RoomId,
                    sentAt = msg.SentAt
                };

                switch (req.Type)
                {
                    case ChatMessageType.Room when req.RoomId != null:
                        await _broadcaster.SendToRoom(req.RoomId, chatPayload);
                        break;
                    case ChatMessageType.Global:
                        await _broadcaster.SendToAll(chatPayload);
                        break;
                    case ChatMessageType.Whisper when req.TargetUserId != null:
                        await _broadcaster.SendToUser(req.TargetUserId, chatPayload);
                        await _broadcaster.SendToUser(currentUserId, chatPayload);
                        break;
                }

                await _gameLogger.LogChat(currentUserId, senderName, req.Message,
                    req.Type.ToString(), req.RoomId);
                break;
            }

            case "ready":
            {
                if (currentUserId == null) break;
                var req = JsonSerializer.Deserialize<ReadyRequest>(dataJson, JsonOptions)!;
                await _roomService.SetReady(req.RoomId, currentUserId, req.IsReady);
                await BroadcastRoomUpdate(req.RoomId);
                break;
            }

            case "start_game":
            {
                if (currentUserId == null) break;
                var req = JsonSerializer.Deserialize<StartGameRequest>(dataJson, JsonOptions)!;
                var room = await _roomService.GetRoom(req.RoomId);

                if (room == null || room.HostUserId != currentUserId)
                {
                    await _broadcaster.SendToUser(currentUserId, new
                    {
                        type = "start_game_result",
                        success = false,
                        error = "Only host can start the game"
                    });
                    break;
                }

                // Game DedicatedServer 실행
                await _roomService.UpdateRoomState(req.RoomId, RoomState.Starting);
                var (process, port) = _dsLauncher.LaunchGameServer(req.RoomId, room.MapName);

                if (process != null)
                {
                    await _roomService.UpdateRoomState(
                        req.RoomId, RoomState.Running, port, process.Id);

                    // 방 전체에 게임 시작 알림 (접속할 서버 정보 포함)
                    await _broadcaster.SendToRoom(req.RoomId, new
                    {
                        type = "game_started",
                        roomId = req.RoomId,
                        serverIp = "127.0.0.1",
                        serverPort = port
                    });

                    await _gameLogger.LogRoomEvent("GAME_START", req.RoomId, currentUserId,
                        null, $"Port:{port} PID:{process.Id} Map:{room.MapName}");
                }
                else
                {
                    await _roomService.UpdateRoomState(req.RoomId, RoomState.Waiting);
                    await _broadcaster.SendToUser(currentUserId, new
                    {
                        type = "start_game_result",
                        success = false,
                        error = "Failed to launch dedicated server"
                    });
                }
                break;
            }

            case "get_rooms":
            {
                if (currentUserId == null) break;
                var rooms = await _roomService.GetRoomList();
                await _broadcaster.SendToUser(currentUserId, new
                {
                    type = "room_list",
                    rooms = rooms.Select(r => new
                    {
                        roomId = r.RoomId,
                        roomName = r.RoomName,
                        hostUserId = r.HostUserId,
                        playerCount = r.Participants.Count,
                        maxPlayers = r.MaxPlayers,
                        mapName = r.MapName,
                        state = r.State.ToString()
                    })
                });
                break;
            }

            case "get_online_users":
            {
                if (currentUserId == null) break;
                var users = await _userService.GetOnlineUsers();
                await _broadcaster.SendToUser(currentUserId, new
                {
                    type = "online_users",
                    users = users.Select(u => new
                    {
                        userId = u.UserId,
                        displayName = u.DisplayName
                    })
                });
                break;
            }
        }

        return currentUserId;
    }

    private async Task HandleDisconnect(string userId)
    {
        _logger.LogInformation("User disconnected: {UserId}", userId);
        var dcUser = await _userService.GetUser(userId);

        // 현재 방에서 퇴장
        var roomId = _connections.GetUserRoom(userId);
        if (roomId != null)
        {
            await _roomService.LeaveRoom(roomId, userId);
            await BroadcastRoomUpdate(roomId);
            await _gameLogger.LogRoomEvent("DISCONNECT_LEAVE", roomId, userId,
                dcUser?.DisplayName ?? userId);
        }

        // 로비 인원 차감
        _dsLauncher.RemoveUserFromLobby(userId);

        await _userService.SetOffline(userId);
        _connections.RemoveConnection(userId);
        await _gameLogger.LogRoomEvent("LOGOUT", "SYSTEM", userId,
            dcUser?.DisplayName ?? userId);

        await BroadcastRoomList();
    }

    private async Task BroadcastRoomList()
    {
        var rooms = await _roomService.GetRoomList();
        await _broadcaster.SendToAll(new
        {
            type = "room_list",
            rooms = rooms.Select(r => new
            {
                roomId = r.RoomId,
                roomName = r.RoomName,
                hostUserId = r.HostUserId,
                playerCount = r.Participants.Count,
                maxPlayers = r.MaxPlayers,
                mapName = r.MapName,
                state = r.State.ToString()
            })
        });
    }

    private async Task BroadcastRoomUpdate(string roomId)
    {
        var room = await _roomService.GetRoom(roomId);
        if (room == null) return;

        await _broadcaster.SendToRoom(roomId, new
        {
            type = "room_update",
            roomId = room.RoomId,
            roomName = room.RoomName,
            hostUserId = room.HostUserId,
            state = room.State.ToString(),
            participants = room.Participants.Select(p => new
            {
                userId = p.UserId,
                displayName = p.DisplayName,
                isReady = p.IsReady
            })
        });
    }
}
