using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.HealthChecks;
using EfCore.FaultIsolation.Services;
using EfCore.FaultIsolation.Stores;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;

namespace Microsoft.Extensions.DependencyInjection;

public static class EfCoreFaultIsolationServiceCollectionExtensions
{
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

public class FaultIsolationOptions
{
    public string? LiteDbConnectionString { get; set; }
    public string HangfireConnectionString { get; set; }
    public int MaxRetries { get; set; } = 5;
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public int HealthCheckIntervalSeconds { get; set; } = 30;
    
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