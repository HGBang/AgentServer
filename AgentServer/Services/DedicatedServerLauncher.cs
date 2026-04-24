using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentServer.Services;

/// <summary>
/// Lobby DS (다중 인스턴스 풀) / Game DS 프로세스를 실행/관리하는 서비스.
/// - Lobby DS: 인원 초과 시 자동 확장, 여유 있는 로비로 배정
/// - Game DS: 방에서 게임 시작 시 실행
/// </summary>
public class DedicatedServerLauncher : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DedicatedServerLauncher> _logger;
    private readonly object _lobbyLock = new();

    private int _nextLobbyPort;
    private int _nextGamePort;
    private int _lobbyIdCounter;

    // Lobby DS 풀: lobbyId -> LobbyInstance
    private readonly ConcurrentDictionary<string, LobbyInstance> _lobbyServers = new();

    // 유저가 어느 로비에 배정됐는지: userId -> lobbyId
    private readonly ConcurrentDictionary<string, string> _userLobbyMap = new();

    // Game DS 관리: roomId -> (process, port)
    private readonly ConcurrentDictionary<string, (Process process, int port)> _gameServers = new();

    public DedicatedServerLauncher(IConfiguration configuration, ILogger<DedicatedServerLauncher> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _nextLobbyPort = _configuration.GetValue("DedicatedServer:Lobby:StartPort", 7777);
        _nextGamePort = _configuration.GetValue("DedicatedServer:Game:StartPort", 7877);
    }

    // ─────────────────────────────────────────
    // Lobby DS 풀
    // ─────────────────────────────────────────

    public string LobbyIp => _configuration.GetValue<string>("DedicatedServer:Lobby:Ip") ?? "127.0.0.1";
    public int MaxPlayersPerLobby => _configuration.GetValue("DedicatedServer:Lobby:MaxPlayers", 100);

    public bool HasAvailableLobby => _lobbyServers.Values.Any(l => l.IsAlive && l.CurrentPlayers < MaxPlayersPerLobby);

    /// <summary>
    /// 서버 시작 시 첫 로비 DS를 실행한다.
    /// </summary>
    public bool LaunchInitialLobby()
    {
        var lobby = LaunchNewLobbyInstance();
        return lobby != null;
    }

    /// <summary>
    /// 유저를 여유 있는 로비에 배정한다.
    /// 모든 로비가 가득 차면 새 로비를 자동 생성한다.
    /// </summary>
    public LobbyInstance? AssignUserToLobby(string userId)
    {
        lock (_lobbyLock)
        {
            // 이미 배정된 유저면 기존 로비 반환
            if (_userLobbyMap.TryGetValue(userId, out var existingLobbyId))
            {
                if (_lobbyServers.TryGetValue(existingLobbyId, out var existing) && existing.IsAlive)
                {
                    return existing;
                }
                // 죽은 로비면 제거
                _userLobbyMap.TryRemove(userId, out _);
            }

            // 여유 있는 로비 찾기 (인원 적은 순)
            var available = _lobbyServers.Values
                .Where(l => l.IsAlive && l.CurrentPlayers < MaxPlayersPerLobby)
                .OrderBy(l => l.CurrentPlayers)
                .FirstOrDefault();

            // 없으면 새 로비 생성
            if (available == null)
            {
                _logger.LogInformation("All lobby servers full. Launching new lobby instance...");
                available = LaunchNewLobbyInstance();

                if (available == null)
                {
                    _logger.LogError("Failed to launch new lobby instance");
                    return null;
                }
            }

            // 배정
            available.CurrentPlayers++;
            _userLobbyMap[userId] = available.LobbyId;

            _logger.LogInformation(
                "User {UserId} assigned to Lobby {LobbyId} (Port:{Port}, Players:{Current}/{Max})",
                userId, available.LobbyId, available.Port, available.CurrentPlayers, MaxPlayersPerLobby);

            return available;
        }
    }

    /// <summary>
    /// 유저가 로비에서 나갈 때 (로그아웃 또는 게임 DS로 이동).
    /// </summary>
    public void RemoveUserFromLobby(string userId)
    {
        lock (_lobbyLock)
        {
            if (_userLobbyMap.TryRemove(userId, out var lobbyId))
            {
                if (_lobbyServers.TryGetValue(lobbyId, out var lobby))
                {
                    lobby.CurrentPlayers = Math.Max(0, lobby.CurrentPlayers - 1);

                    _logger.LogInformation(
                        "User {UserId} removed from Lobby {LobbyId} (Players:{Current}/{Max})",
                        userId, lobbyId, lobby.CurrentPlayers, MaxPlayersPerLobby);
                }
            }
        }
    }

    /// <summary>
    /// 새 로비 DS 인스턴스를 실행한다.
    /// </summary>
    private LobbyInstance? LaunchNewLobbyInstance()
    {
        var exePath = _configuration.GetValue<string>("DedicatedServer:Lobby:ExePath");
        var mapName = _configuration.GetValue<string>("DedicatedServer:Lobby:MapName") ?? "/Game/Maps/Lobby";
        var additionalArgs = _configuration.GetValue<string>("DedicatedServer:Lobby:AdditionalArgs") ?? "";

        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogError("DedicatedServer:Lobby:ExePath is not configured");
            return null;
        }

        if (!File.Exists(exePath))
        {
            _logger.LogError("Lobby DS exe not found: {ExePath}", exePath);
            return null;
        }

        var port = Interlocked.Increment(ref _nextLobbyPort);
        var lobbyId = $"Lobby_{Interlocked.Increment(ref _lobbyIdCounter)}";
        var arguments = $"{mapName} -server -log -port={port} -LobbyId={lobbyId} {additionalArgs}";

        _logger.LogInformation("Launching Lobby DS [{LobbyId}]: {ExePath} {Args}", lobbyId, exePath, arguments);

        try
        {
            var process = LaunchProcess(exePath, arguments, lobbyId);

            if (process != null)
            {
                var lobby = new LobbyInstance
                {
                    LobbyId = lobbyId,
                    Process = process,
                    Port = port,
                    CurrentPlayers = 0
                };

                _lobbyServers[lobbyId] = lobby;

                _logger.LogInformation("Lobby DS [{LobbyId}] started: PID={Pid}, Port={Port}",
                    lobbyId, process.Id, port);

                return lobby;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Lobby DS [{LobbyId}]", lobbyId);
        }

        return null;
    }

    public void StopLobbyServer(string lobbyId)
    {
        if (_lobbyServers.TryRemove(lobbyId, out var lobby))
        {
            StopProcess(lobby.Process, lobbyId);

            // 해당 로비에 배정된 유저 정리
            var usersToRemove = _userLobbyMap
                .Where(kv => kv.Value == lobbyId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var uid in usersToRemove)
            {
                _userLobbyMap.TryRemove(uid, out _);
            }
        }
    }

    public void StopAllLobbyServers()
    {
        foreach (var lobbyId in _lobbyServers.Keys.ToList())
        {
            StopLobbyServer(lobbyId);
        }
    }

    /// <summary>
    /// 현재 로비 현황 조회.
    /// </summary>
    public List<LobbyInstance> GetLobbyStatus()
    {
        return _lobbyServers.Values
            .Where(l => l.IsAlive)
            .OrderBy(l => l.Port)
            .ToList();
    }

    // ─────────────────────────────────────────
    // Game DS
    // ─────────────────────────────────────────

    public (Process? process, int port) LaunchGameServer(string roomId, string mapName)
    {
        var exePath = _configuration.GetValue<string>("DedicatedServer:Game:ExePath");
        var additionalArgs = _configuration.GetValue<string>("DedicatedServer:Game:AdditionalArgs") ?? "";

        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogError("DedicatedServer:Game:ExePath is not configured");
            return (null, 0);
        }

        if (!File.Exists(exePath))
        {
            _logger.LogError("Game DS exe not found: {ExePath}", exePath);
            return (null, 0);
        }

        var port = Interlocked.Increment(ref _nextGamePort);
        var arguments = $"{mapName} -server -log -port={port} -RoomId={roomId} {additionalArgs}";

        _logger.LogInformation("Launching Game DS: {ExePath} {Args}", exePath, arguments);

        try
        {
            var process = LaunchProcess(exePath, arguments, $"GAME-{roomId}");

            if (process != null)
            {
                _gameServers[roomId] = (process, port);
                _logger.LogInformation("Game DS started: PID={Pid}, Port={Port}, Room={RoomId}",
                    process.Id, port, roomId);
                return (process, port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Game DS for room {RoomId}", roomId);
        }

        return (null, 0);
    }

    public void StopGameServer(string roomId)
    {
        if (_gameServers.TryRemove(roomId, out var server))
        {
            StopProcess(server.process, $"GAME-{roomId}");
        }
    }

    public (int port, int pid)? GetGameServerInfo(string roomId)
    {
        if (_gameServers.TryGetValue(roomId, out var server) && !server.process.HasExited)
        {
            return (server.port, server.process.Id);
        }
        return null;
    }

    // ─────────────────────────────────────────
    // 공통
    // ─────────────────────────────────────────

    private Process? LaunchProcess(string exePath, string arguments, string tag)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = Process.Start(processInfo);

        if (process != null)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogDebug("[DS-{Tag}] {Output}", tag, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("[DS-{Tag}] {Error}", tag, e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }

    private void StopProcess(Process process, string tag)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _logger.LogInformation("[DS-{Tag}] Stopped: PID={Pid}", tag, process.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DS-{Tag}] Could not stop PID={Pid}", tag, process.Id);
        }
    }

    public void Dispose()
    {
        StopAllLobbyServers();
        foreach (var roomId in _gameServers.Keys.ToList())
        {
            StopGameServer(roomId);
        }
    }
}

/// <summary>
/// 로비 DS 인스턴스 정보.
/// </summary>
public class LobbyInstance
{
    public string LobbyId { get; set; } = string.Empty;
    public Process Process { get; set; } = null!;
    public int Port { get; set; }
    public int CurrentPlayers { get; set; }
    public bool IsAlive => Process is { HasExited: false };
}
