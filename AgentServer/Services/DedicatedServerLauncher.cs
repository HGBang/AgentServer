using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentServer.Services;

/// <summary>
/// Lobby DS / Game DS 프로세스를 실행/관리하는 서비스.
/// - Lobby DS: 서버 시작 시 자동 실행, 로그인 성공 시 접속 정보 제공
/// - Game DS: 방에서 게임 시작 시 실행
/// </summary>
public class DedicatedServerLauncher : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DedicatedServerLauncher> _logger;
    private int _nextGamePort;

    // Lobby DS 정보
    private Process? _lobbyProcess;
    private int _lobbyPort;
    private bool _lobbyReady;

    // Game DS 관리: roomId -> (process, port)
    private readonly ConcurrentDictionary<string, (Process process, int port)> _gameServers = new();

    public DedicatedServerLauncher(IConfiguration configuration, ILogger<DedicatedServerLauncher> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _nextGamePort = _configuration.GetValue("DedicatedServer:Game:StartPort", 7787);
    }

    // ─────────────────────────────────────────
    // Lobby DS
    // ─────────────────────────────────────────

    public bool IsLobbyReady => _lobbyReady && _lobbyProcess is { HasExited: false };

    public int LobbyPort => _lobbyPort;

    public string LobbyIp => _configuration.GetValue<string>("DedicatedServer:Lobby:Ip") ?? "127.0.0.1";

    public bool LaunchLobbyServer()
    {
        if (IsLobbyReady)
        {
            _logger.LogInformation("Lobby DS is already running on port {Port}", _lobbyPort);
            return true;
        }

        var exePath = _configuration.GetValue<string>("DedicatedServer:Lobby:ExePath");
        var mapName = _configuration.GetValue<string>("DedicatedServer:Lobby:MapName") ?? "/Game/Maps/Lobby";
        _lobbyPort = _configuration.GetValue("DedicatedServer:Lobby:Port", 7777);
        var additionalArgs = _configuration.GetValue<string>("DedicatedServer:Lobby:AdditionalArgs") ?? "";

        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogError("DedicatedServer:Lobby:ExePath is not configured");
            return false;
        }

        if (!File.Exists(exePath))
        {
            _logger.LogError("Lobby DS exe not found: {ExePath}", exePath);
            return false;
        }

        var arguments = $"{mapName} -server -log -port={_lobbyPort} {additionalArgs}";

        _logger.LogInformation("Launching Lobby DS: {ExePath} {Args}", exePath, arguments);

        try
        {
            _lobbyProcess = LaunchProcess(exePath, arguments, "LOBBY");

            if (_lobbyProcess != null)
            {
                _lobbyReady = true;
                _logger.LogInformation("Lobby DS started: PID={Pid}, Port={Port}",
                    _lobbyProcess.Id, _lobbyPort);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Lobby DS");
        }

        return false;
    }

    public void StopLobbyServer()
    {
        if (_lobbyProcess != null)
        {
            StopProcess(_lobbyProcess, "LOBBY");
            _lobbyProcess = null;
            _lobbyReady = false;
        }
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

    public void StopServer(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _logger.LogInformation("DedicatedServer stopped: PID={Pid}", pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop DedicatedServer PID={Pid}", pid);
        }
    }

    public void Dispose()
    {
        StopLobbyServer();
        foreach (var roomId in _gameServers.Keys.ToList())
        {
            StopGameServer(roomId);
        }
    }
}
