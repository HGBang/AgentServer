using AgentServer.Data;
using AgentServer.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentServer.Services;

public class UserService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UserService> _logger;

    public UserService(IServiceProvider serviceProvider, ILogger<UserService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<User> LoginOrRegister(string userId, string displayName)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);

        if (user == null)
        {
            user = new User
            {
                UserId = userId,
                DisplayName = displayName,
                IsOnline = true
            };
            db.Users.Add(user);
            _logger.LogInformation("New user registered: {UserId} ({DisplayName})", userId, displayName);
        }
        else
        {
            user.DisplayName = displayName;
            user.LastLoginAt = DateTime.UtcNow;
            user.IsOnline = true;
            _logger.LogInformation("User logged in: {UserId}", userId);
        }

        await db.SaveChangesAsync();
        return user;
    }

    public async Task SetOffline(string userId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (user != null)
        {
            user.IsOnline = false;
            await db.SaveChangesAsync();
        }
    }

    public async Task<User?> GetUser(string userId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
    }

    public async Task<List<User>> GetOnlineUsers()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Users.Where(u => u.IsOnline).ToListAsync();
    }
}
