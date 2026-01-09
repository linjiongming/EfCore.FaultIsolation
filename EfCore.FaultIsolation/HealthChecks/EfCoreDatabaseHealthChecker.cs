using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.HealthChecks;

/// <summary>
/// EF Core 数据库健康检查实现
/// </summary>
/// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
public class EfCoreDatabaseHealthChecker<TDbContext> : IDatabaseHealthChecker<TDbContext> where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly ILogger<EfCoreDatabaseHealthChecker<TDbContext>> _logger;
    private Timer? _monitoringTimer;
    private bool _lastHealthStatus = false;
    
    /// <inheritdoc />
    public event EventHandler? DatabaseConnected;
    
    /// <summary>
    /// 初始化 EfCoreDatabaseHealthChecker 实例
    /// </summary>
    /// <param name="dbContext">数据库上下文实例</param>
    /// <param name="logger">日志记录器</param>
    public EfCoreDatabaseHealthChecker(TDbContext dbContext, ILogger<EfCoreDatabaseHealthChecker<TDbContext>> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }
    
    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // 使用EF Core提供的CanConnectAsync方法，这是一个轻量级的连接检查
            return await _dbContext.Database.CanConnectAsync();
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
        if (_monitoringTimer != null)
        {
            _monitoringTimer.Dispose();
            _monitoringTimer = null;
        }
    }
    
    private async Task CheckDatabaseHealthAsync()
    {
        var isHealthy = await IsHealthyAsync();
        
        if (isHealthy && !_lastHealthStatus)
        {
            DatabaseConnected?.Invoke(this, EventArgs.Empty);
        }
        
        _lastHealthStatus = isHealthy;
    }
    

}