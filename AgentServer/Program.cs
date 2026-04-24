using System.Net.WebSockets;
using AgentServer.Data;
using AgentServer.Handlers;
using AgentServer.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                      ?? "Data Source=agentserver.db"));

// ── Services ──
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<DedicatedServerLauncher>();
builder.Services.AddSingleton<GameLogger>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<MessageBroadcaster>();
builder.Services.AddScoped<WebSocketHandler>();

// ── CORS (Unreal HTTP 요청 허용) ──
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// ── DB 자동 생성 ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// ── WebSocket 엔드포인트 ──
app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connections only");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    using var scope = app.Services.CreateScope();
    var handler = scope.ServiceProvider.GetRequiredService<WebSocketHandler>();
    await handler.HandleConnection(webSocket);
});

// ── Health Check / Status API ──
app.MapGet("/", (ConnectionManager cm) => new
{
    server = "AgentServer",
    status = "running",
    connections = cm.ConnectionCount,
    timestamp = DateTime.UtcNow
});

app.MapGet("/api/status", (ConnectionManager cm) => new
{
    connections = cm.ConnectionCount,
    uptime = DateTime.UtcNow
});

var port = builder.Configuration.GetValue("ServerPort", 5000);
app.Urls.Add($"http://0.0.0.0:{port}");

Console.WriteLine($"===========================================");
Console.WriteLine($"  AgentServer started on port {port}");
Console.WriteLine($"  WebSocket: ws://localhost:{port}/ws");
Console.WriteLine($"  Status:    http://localhost:{port}/");
Console.WriteLine($"===========================================");

app.Run();
