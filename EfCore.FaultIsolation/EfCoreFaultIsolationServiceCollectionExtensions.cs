using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.HealthChecks;
using EfCore.FaultIsolation.Services;
using EfCore.FaultIsolation.Stores;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 服务集合扩展，用于注册故障隔离相关服务
/// </summary>
public static class EfCoreFaultIsolationServiceCollectionExtensions
{
    /// <summary>
    /// 添加EF Core故障隔离服务到依赖注入容器
    /// </summary>
    /// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
    /// <param name="services">服务集合</param>
    /// <param name="configureOptions">故障隔离选项配置委托</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddEfCoreFaultIsolation<TDbContext>(
        this IServiceCollection services,
        Action<FaultIsolationOptions>? configureOptions = null
    ) where TDbContext : DbContext
    {
        var options = new FaultIsolationOptions();
        configureOptions?.Invoke(options);

        // 注册存储服务
        services.AddSingleton<IFaultIsolationStore<TDbContext>>(_ =>
            new LiteDbStore<TDbContext>(options.LiteDbConnectionString));

        // 注册健康检查服务
        services.AddScoped<IDatabaseHealthChecker<TDbContext>, EfCoreDatabaseHealthChecker<TDbContext>>();

        // 注册核心服务
        services.AddScoped<IRetryService>((sp) => new RetryService(options.InitialRetryDelay, sp));
        services.AddScoped<HangfireSchedulerService>();
        services.AddScoped<EfCoreFaultIsolationService<TDbContext>>();
        services.AddScoped<RetryJobService>();

        // 配置Hangfire服务但不添加Hangfire服务器
        // 在测试环境中，Hangfire服务器可能会导致一些问题
        services.AddHangfire(configuration =>
        {
            configuration.UseStorage(new Hangfire.Storage.SQLite.SQLiteStorage(options.HangfireConnectionString));
        });

        // 检查是否已经注册了HangfireServer，如果没有则添加
        if (!services.Any(descriptor => descriptor.ImplementationType?.FullName == "Hangfire.HangfireServer"))
        {
            services.AddHangfireServer();
        }

        return services;
    }
}

/// <summary>
/// 故障隔离选项配置类
/// </summary>
public class FaultIsolationOptions
{
    /// <summary>
    /// LiteDB 连接字符串
    /// </summary>
    public string? LiteDbConnectionString { get; set; }
    
    /// <summary>
    /// Hangfire 连接字符串
    /// </summary>
    public string HangfireConnectionString { get; set; }
    
    /// <summary>
    /// 最大重试次数
    /// </summary>
    public int MaxRetries { get; set; } = 5;
    
    /// <summary>
    /// 初始重试延迟时间
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    
    /// <summary>
    /// 健康检查间隔（秒）
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;
    
    /// <summary>
    /// 初始化 FaultIsolationOptions 实例
    /// </summary>
    public FaultIsolationOptions()
    {
        // Create fault directory if it doesn't exist
        string faultDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fault");
        Directory.CreateDirectory(faultDirectory);
        
        // Set default Hangfire connection string to use fault directory
        HangfireConnectionString = Path.Combine(faultDirectory, "hangfire.db");
    }

    internal System.Collections.Generic.HashSet<Type> IsolatedEntities { get; } = new();

    /// <summary>
    /// 添加需要故障隔离的实体类型
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    public void AddIsolatedEntity<TEntity>() where TEntity : class
    {
        IsolatedEntities.Add(typeof(TEntity));
    }

    /// <summary>
    /// 检查指定类型是否需要故障隔离
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <returns>是否需要故障隔离</returns>
    public bool IsEntityIsolated<TEntity>() where TEntity : class
    {
        return IsolatedEntities.Count == 0 || IsolatedEntities.Contains(typeof(TEntity));
    }
}