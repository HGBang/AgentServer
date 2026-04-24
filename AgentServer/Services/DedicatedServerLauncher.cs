using System.Diagnostics;

namespace AgentServer.Services;

/// <summary>
/// DedicatedServer.exe 프로세스를 실행/관리하는 서비스.
/// appsettings.json의 DedicatedServer 섹션에서 경로 설정.
/// </summary>
public class DedicatedServerLauncher
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DedicatedServerLauncher> _logger;
    private int _nextPort;

    public DedicatedServerLauncher(IConfiguration configuration, ILogger<DedicatedServerLauncher> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _nextPort = _configuration.GetValue("DedicatedServer:StartPort", 7777);
    }

    public (Process? process, int port) LaunchServer(string roomId, string mapName)
    {
        var exePath = _configuration.GetValue<string>("DedicatedServer:ExePath");

        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogError("DedicatedServer:ExePath is not configured in appsettings.json");
            return (null, 0);
        }

        if (!File.Exists(exePath))
        {
            _logger.LogError("DedicatedServer exe not found: {ExePath}", exePath);
            return (null, 0);
        }

        var port = Interlocked.Increment(ref _nextPort);
        var additionalArgs = _configuration.GetValue<string>("DedicatedServer:AdditionalArgs") ?? "";

        // UE DedicatedServer 실행 인자
        var arguments = $"{mapName} -server -log -port={port} -RoomId={roomId} {additionalArgs}";

        _logger.LogInformation("Launching DedicatedServer: {ExePath} {Args}", exePath, arguments);

        try
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
                _logger.LogInformation(
                    "DedicatedServer started: PID={Pid}, Port={Port}, Room={RoomId}",
                    process.Id, port, roomId);

                // 비동기로 출력 로그 캡처
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.LogDebug("[DS-{RoomId}] {Output}", roomId, e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.LogWarning("[DS-{RoomId}] {Error}", roomId, e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                return (process, port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch DedicatedServer for room {RoomId}", roomId);
        }

        return (null, 0);
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
}
