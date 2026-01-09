using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.HealthChecks;

public class EfCoreDatabaseHealthChecker<TDbContext> : IDatabaseHealthChecker where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly ILogger<EfCoreDatabaseHealthChecker<TDbContext>> _logger;
    private Timer? _monitoringTimer;
    private bool _lastHealthStatus = false;
    
    public event EventHandler? DatabaseConnected;
    
    public EfCoreDatabaseHealthChecker(TDbContext dbContext, ILogger<EfCoreDatabaseHealthChecker<TDbContext>> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        
        // 添加对数据库连接状态变化事件的监听
        try
        {
            // 确保连接已创建
            if (_dbContext.Database.GetDbConnection().State == System.Data.ConnectionState.Closed)
            {
                _dbContext.Database.GetDbConnection().Open();
                _dbContext.Database.GetDbConnection().Close();
            }
            
            // 订阅连接状态变化事件
            _dbContext.Database.GetDbConnection().StateChange += OnConnectionStateChanged;
        }
        catch (Exception ex)
        {
            // 记录异常但不影响主流程
            _logger.LogError(ex, "Failed to subscribe to connection state change event: {Message}", ex.Message);
        }
    }
    
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            await _dbContext.Database.OpenConnectionAsync();
            await _dbContext.Database.CloseConnectionAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
    
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
    
    private void OnConnectionStateChanged(object sender, System.Data.StateChangeEventArgs e)
    {
        // 当连接从关闭状态变为打开状态时，触发DatabaseConnected事件
        if (e.CurrentState == System.Data.ConnectionState.Open && e.OriginalState == System.Data.ConnectionState.Closed)
        {
            _logger.LogInformation("Database connection recovered, triggering immediate retry");
            DatabaseConnected?.Invoke(this, EventArgs.Empty);
        }
    }
}