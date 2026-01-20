using EfCore.FaultIsolation.HealthChecks;
using EfCore.FaultIsolation.Interceptors;
using EfCore.FaultIsolation.Services;
using EfCore.FaultIsolation.Stores;
using Hangfire;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
    public static IServiceCollection AddFaultIsolation<TDbContext>(
        this IServiceCollection services,
        Action<FaultIsolationOptions>? configureOptions = null
    ) where TDbContext : DbContext
    {
        var options = new FaultIsolationOptions();
        configureOptions?.Invoke(options);

        // 注册选项配置
        services.AddSingleton(options);

        // 注册存储服务
        services.AddSingleton<IFaultIsolationStore<TDbContext>>(_ =>
            new LiteDbStore<TDbContext>(options.LiteDbConnectionString));

        // 注册健康检查服务
        services.AddScoped<IDatabaseHealthChecker<TDbContext>, EfCoreDatabaseHealthChecker<TDbContext>>();

        // 注册故障隔离拦截器
        services.AddScoped<FaultIsolationInterceptor>();

        // 注册核心服务
        services.AddScoped<IRetryService>((sp) => new RetryService(sp, options.InitialRetryDelay));
        services.AddScoped<HangfireSchedulerService>();
        services.AddScoped<RetryJobService>();
        services.AddHostedService<FaultIsolationService<TDbContext>>();

        // 配置Hangfire服务
        services.AddHangfire(configuration =>
        {
            configuration.UseStorage(new Hangfire.Storage.SQLite.SQLiteStorage(options.HangfireConnectionString));
        });
        services.AddHangfireServer();

        return services;
    }

    /// <summary>
    /// 为DbContext添加故障隔离拦截器
    /// </summary>
    /// <param name="optionsBuilder">DbContext选项构建器</param>
    /// <param name="serviceProvider">服务提供程序</param>
    /// <returns>DbContext选项构建器</returns>
    public static DbContextOptionsBuilder UseEfCoreFaultIsolation(
        this DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider
    )
    {
        // 获取故障隔离拦截器
        var faultIsolationInterceptor = serviceProvider.GetRequiredService<FaultIsolationInterceptor>();
        optionsBuilder.AddInterceptors(faultIsolationInterceptor);

        return optionsBuilder;
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
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

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

    internal System.Collections.Generic.HashSet<Type> IsolatedEntities { get; } = [];

    /// <summary>
    /// 需要捕获的变更类型集合
    /// </summary>
    internal System.Collections.Generic.HashSet<EntityState> CapturedChangeTypes { get; } = [];

    /// <summary>
    /// 实体类型到变更类型集合的映射
    /// </summary>
    internal System.Collections.Generic.Dictionary<Type, HashSet<EntityState>> EntityChangeTypes { get; } = [];

    /// <summary>
    /// 添加需要故障隔离的实体类型并返回配置器
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <returns>实体配置器</returns>
    public EntityConfiguration<TEntity> AddIsolatedEntity<TEntity>() where TEntity : class
    {
        var entityType = typeof(TEntity);
        IsolatedEntities.Add(entityType);
        return new EntityConfiguration<TEntity>(this);
    }

    /// <summary>
    /// 实体配置器类，用于支持流畅API
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    public class EntityConfiguration<TEntity> where TEntity : class
    {
        private readonly FaultIsolationOptions _options;
        private readonly Type _entityType = typeof(TEntity);

        /// <summary>
        /// 初始化实体配置器
        /// </summary>
        /// <param name="options">故障隔离选项</param>
        internal EntityConfiguration(FaultIsolationOptions options)
        {
            _options = options;
        }

        /// <summary>
        /// 为实体类型配置需要捕获的变更类型
        /// </summary>
        /// <param name="changeTypes">变更类型数组</param>
        /// <returns>当前配置器实例</returns>
        public EntityConfiguration<TEntity> CaptureChangeTypes(params EntityState[] changeTypes)
        {
            if (!_options.EntityChangeTypes.ContainsKey(_entityType))
            {
                _options.EntityChangeTypes[_entityType] = [];
            }

            foreach (var changeType in changeTypes)
            {
                _options.EntityChangeTypes[_entityType].Add(changeType);
            }

            return this;
        }
    }

    /// <summary>
    /// 添加需要故障隔离的实体类型（非泛型版本，保持向后兼容）
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>当前选项实例</returns>
    public FaultIsolationOptions AddIsolatedEntity(Type entityType)
    {
        IsolatedEntities.Add(entityType);
        return this;
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

    /// <summary>
    /// 检查指定类型是否需要故障隔离
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>是否需要故障隔离</returns>
    public bool IsEntityIsolated(Type entityType)
    {
        return IsolatedEntities.Count == 0 || IsolatedEntities.Contains(entityType);
    }

    /// <summary>
    /// 添加需要捕获的变更类型（全局配置）
    /// </summary>
    /// <param name="changeTypes">变更类型数组</param>
    public void AddCapturedChangeTypes(params EntityState[] changeTypes)
    {
        foreach (var changeType in changeTypes)
        {
            CapturedChangeTypes.Add(changeType);
        }
    }

    /// <summary>
    /// 为特定实体类型添加需要捕获的变更类型
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="changeTypes">变更类型数组</param>
    public void AddCapturedChangeTypes<TEntity>(params EntityState[] changeTypes) where TEntity : class
    {
        var entityType = typeof(TEntity);
        if (!EntityChangeTypes.ContainsKey(entityType))
        {
            EntityChangeTypes[entityType] = [];
        }
        
        foreach (var changeType in changeTypes)
        {
            EntityChangeTypes[entityType].Add(changeType);
        }
    }

    /// <summary>
    /// 检查指定的变更类型是否需要捕获
    /// </summary>
    /// <param name="changeType">变更类型</param>
    /// <returns>是否需要捕获</returns>
    public bool IsChangeTypeCaptured(EntityState changeType)
    {
        return CapturedChangeTypes.Count == 0 || CapturedChangeTypes.Contains(changeType);
    }

    /// <summary>
    /// 检查指定实体类型的变更类型是否需要捕获
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="changeType">变更类型</param>
    /// <returns>是否需要捕获</returns>
    public bool IsChangeTypeCaptured<TEntity>(EntityState changeType) where TEntity : class
    {
        var entityType = typeof(TEntity);
        
        // 如果有实体类型特定的配置，则使用该配置
        if (EntityChangeTypes.ContainsKey(entityType))
        {
            return EntityChangeTypes[entityType].Contains(changeType);
        }
        
        // 否则使用全局配置
        return IsChangeTypeCaptured(changeType);
    }

    /// <summary>
    /// 检查指定实体类型的变更类型是否需要捕获
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="changeType">变更类型</param>
    /// <returns>是否需要捕获</returns>
    public bool IsChangeTypeCaptured(Type entityType, EntityState changeType)
    {
        // 如果有实体类型特定的配置，则使用该配置
        if (EntityChangeTypes.ContainsKey(entityType))
        {
            return EntityChangeTypes[entityType].Contains(changeType);
        }
        
        // 否则使用全局配置
        return IsChangeTypeCaptured(changeType);
    }
}