using System.Collections.Concurrent;

namespace AgentServer.Services;

/// <summary>
/// 채팅 내역 및 Room 이벤트를 파일 로그로 보관.
/// Logs/Chat_yyyy-MM-dd.log
/// Logs/Room_yyyy-MM-dd.log
/// </summary>
public class GameLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new();
    private readonly object _lock = new();

    public GameLogger(IConfiguration configuration)
    {
        _logDirectory = configuration.GetValue<string>("Logging:GameLogPath") ?? "Logs";
        Directory.CreateDirectory(_logDirectory);
    }

    public async Task LogChat(string senderId, string senderName, string message, string chatType, string? roomId)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var roomInfo = roomId != null ? $"[Room:{roomId}]" : "[Global]";
        var line = $"[{timestamp}] [{chatType}] {roomInfo} {senderName}({senderId}): {message}";

        await AppendLog("Chat", line);
    }

    public async Task LogRoomEvent(string eventType, string roomId, string? userId = null,
        string? displayName = null, string? detail = null)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var userInfo = userId != null ? $"{displayName}({userId})" : "";
        var detailInfo = detail != null ? $" | {detail}" : "";
        var line = $"[{timestamp}] [{eventType}] Room:{roomId} {userInfo}{detailInfo}";

        await AppendLog("Room", line);
    }

    private Task AppendLog(string category, string line)
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var fileName = $"{category}_{date}.log";
        var filePath = Path.Combine(_logDirectory, fileName);

        lock (_lock)
        {
            using var writer = new StreamWriter(filePath, append: true);
            writer.WriteLine(line);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
        _writers.Clear();
    }
}
