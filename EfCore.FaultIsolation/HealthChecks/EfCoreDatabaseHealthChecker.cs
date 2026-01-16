using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.HealthChecks;

/// <summary>
/// EF Core 数据库健康检查实现
/// </summary>
/// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
/// <remarks>
/// 初始化 EfCoreDatabaseHealthChecker 实例
/// </remarks>
/// <param name="logger">日志记录器</param>
/// <param name="dbContext">数据库上下文实例</param>
public class EfCoreDatabaseHealthChecker<TDbContext>(
    ILogger<EfCoreDatabaseHealthChecker<TDbContext>> logger,
    TDbContext dbContext) : IDatabaseHealthChecker<TDbContext>
    where TDbContext : DbContext
{
    private Timer? _monitoringTimer;
    private bool _lastHealthStatus = false;

    /// <inheritdoc />
    public event EventHandler? DatabaseConnected;

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // 使用EF Core提供的CanConnectAsync方法，这是一个轻量级的连接检查
            return await dbContext.Database.CanConnectAsync();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void StartMonitoring(int intervalSeconds = 30)
    {
        StopMonitoring();

        _monitoringTimer = new Timer(
            async _ => await CheckDatabaseHealthAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(intervalSeconds)
        );
    }

    /// <inheritdoc />
    public void StopMonitoring()
    {
        _monitoringTimer?.Dispose();
        _monitoringTimer = null;
    }

    private async Task CheckDatabaseHealthAsync()
    {
        var isHealthy = await IsHealthyAsync();

        if (isHealthy && !_lastHealthStatus)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Database connection restored for DbContext: {DbContextType}", typeof(TDbContext).Name);
            }
            DatabaseConnected?.Invoke(this, EventArgs.Empty);
        }

        _lastHealthStatus = isHealthy;
    }
}