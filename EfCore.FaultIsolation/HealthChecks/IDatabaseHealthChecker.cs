using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EfCore.FaultIsolation.HealthChecks;

/// <summary>
/// 数据库健康检查接口
/// </summary>
/// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
public interface IDatabaseHealthChecker<TDbContext> where TDbContext : DbContext
{
    /// <summary>
    /// 当数据库连接恢复时触发的事件
    /// </summary>
    event EventHandler DatabaseConnected;
    
    /// <summary>
    /// 检查数据库是否健康
    /// </summary>
    /// <returns>数据库是否健康</returns>
    Task<bool> IsHealthyAsync();
    
    /// <summary>
    /// 开始监控数据库健康状态
    /// </summary>
    /// <param name="intervalSeconds">监控间隔（秒）</param>
    void StartMonitoring(int intervalSeconds = 30);
    
    /// <summary>
    /// 停止监控数据库健康状态
    /// </summary>
    void StopMonitoring();
}
