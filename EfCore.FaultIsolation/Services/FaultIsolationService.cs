using EfCore.FaultIsolation.HealthChecks;
using EfCore.FaultIsolation.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;
using System.Reflection;

namespace EfCore.FaultIsolation.Services;

/// <summary>
/// EF Core 故障隔离服务，用于处理实体操作的故障隔离和自动重试
/// </summary>
/// <typeparam name="TDbContext">数据库上下文类型</typeparam>
public class FaultIsolationService<TDbContext>(
    IServiceProvider serviceProvider,
    ILogger<FaultIsolationService<TDbContext>> logger,
    IDatabaseHealthChecker<TDbContext> healthChecker,
    HangfireSchedulerService schedulerService) : IHostedService where TDbContext : DbContext
{
    private readonly Dictionary<string, Type> _entityTypeMap = [];

    // 缓存用于获取FaultStore的委托，避免重复使用反射
    private readonly Dictionary<Type, Func<object>> _faultStoreFactories = [];

    // 缓存用于获取所有Fault集合名称的委托
    private readonly Dictionary<Type, Func<object, CancellationToken, Task<IEnumerable<string>>>> _getAllFaultCollectionNamesFactories = [];

    private IFaultIsolationStore<TDbContext> GetFaultStore()
    {
        return serviceProvider.GetRequiredService<IFaultIsolationStore<TDbContext>>();
    }

    /// <summary>
    /// 为给定的DbContext类型生成获取FaultStore的委托
    /// </summary>
    /// <param name="dbContextType">DbContext类型</param>
    /// <returns>获取FaultStore的委托</returns>
    private Func<object> CreateFaultStoreFactory(Type dbContextType)
    {
        // 检查缓存中是否已有委托
        if (_faultStoreFactories.TryGetValue(dbContextType, out var factory))
        {
            return factory;
        }

        var getFaultStoreMethod = GetType().GetMethod(nameof(GetFaultStore), BindingFlags.NonPublic | BindingFlags.Instance);
        ArgumentNullException.ThrowIfNull(getFaultStoreMethod, "GetFaultStore method");

        var genericGetFaultStoreMethod = getFaultStoreMethod.MakeGenericMethod(dbContextType);

        // 创建实例参数
        var instanceParam = Expression.Constant(this);

        // 创建方法调用表达式
        var methodCallExpr = Expression.Call(instanceParam, genericGetFaultStoreMethod);

        // 转换为object类型
        var convertExpr = Expression.Convert(methodCallExpr, typeof(object));

        // 编译为委托
        var lambda = Expression.Lambda<Func<object>>(convertExpr);
        factory = lambda.Compile();

        // 缓存委托
        _faultStoreFactories[dbContextType] = factory;

        return factory;
    }

    /// <summary>
    /// 为给定的FaultStore类型生成获取所有Fault集合名称的委托
    /// </summary>
    /// <param name="faultStoreType">FaultStore类型</param>
    /// <returns>获取所有Fault集合名称的委托</returns>
    private Func<object, CancellationToken, Task<IEnumerable<string>>> CreateGetAllFaultCollectionNamesFactory(Type faultStoreType)
    {
        // 检查缓存中是否已有委托
        if (_getAllFaultCollectionNamesFactories.TryGetValue(faultStoreType, out var factory))
        {
            return factory;
        }

        // 获取GetAllFaultCollectionNamesAsync方法
        var getAllFaultCollectionNamesMethod = faultStoreType.GetMethod(nameof(IFaultIsolationStore<>.GetAllFaultCollectionNamesAsync));
        if (getAllFaultCollectionNamesMethod == null)
        {
            logger.LogError("Could not find GetAllFaultCollectionNamesAsync method on fault store");
            return (_, _) => Task.FromResult(Enumerable.Empty<string>());
        }

        // 创建参数表达式
        var faultStoreParam = Expression.Parameter(typeof(object), "faultStore");
        var cancellationTokenParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        // 将faultStore参数转换为实际类型
        var convertExpr = Expression.Convert(faultStoreParam, faultStoreType);

        // 创建方法调用表达式
        var methodCallExpr = Expression.Call(
            convertExpr,
            getAllFaultCollectionNamesMethod,
            cancellationTokenParam
        );

        // 编译为委托
        var lambda = Expression.Lambda<Func<object, CancellationToken, Task<IEnumerable<string>>>>(
            methodCallExpr,
            faultStoreParam,
            cancellationTokenParam
        );
        factory = lambda.Compile();

        // 缓存委托
        _getAllFaultCollectionNamesFactories[faultStoreType] = factory;

        return factory;
    }

    /// <summary>
    /// 注册数据库连接恢复事件
    /// </summary>
    private void RegisterDatabaseConnectedEvent()
    {
        try
        {
            healthChecker.DatabaseConnected += (sender, args) =>
            {
                logger.LogInformation("Database connection recovered, triggering immediate batch retry for all entities");

                // 当数据库连接恢复时，为所有实体类型触发立即重试
                var dbContextType = typeof(TDbContext);
                foreach (var (entityTypeName, entityType) in _entityTypeMap)
                {
                    try
                    {
                        const int batchSize = 100;
                        schedulerService.TriggerImmediateBatchRetry(entityType, dbContextType, batchSize);
                        logger.LogInformation("Triggered immediate batch retry for entity type {EntityType} and DbContext type {DbContextType}",
                            entityType.FullName, dbContextType.FullName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to trigger immediate batch retry for entity type {EntityType} and DbContext type {DbContextType}",
                            entityType.FullName, dbContextType.FullName);
                    }
                }
            };

            logger.LogInformation("Database connected event registered successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register database connected event");
        }
    }

    /// <summary>
    /// 启动故障隔离服务，执行以下操作：
    /// 1. 扫描当前程序集及所有引用程序集，收集所有实体类型和DbContext类型
    /// 2. 恢复所有Fault集合的重试任务，为每个集合安排Hangfire批处理重试
    /// 3. 自动配置健康检查监控，为所有DbContext类型注册连接恢复事件处理
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting FaultIsolationService");

        // 扫描当前程序集及所有引用程序集，收集所有实体类型
        ScanEntityTypes();

        // 恢复所有Fault集合的重试任务，为每个集合安排Hangfire批处理重试
        await RecoverPendingRetryTasksAsync(cancellationToken);

        // 注册DatabaseConnected事件
        RegisterDatabaseConnectedEvent();

        // 启动健康检查监控
        healthChecker.StartMonitoring(30);
        logger.LogInformation("Health check monitoring started");

        logger.LogInformation("FaultIsolationService started successfully");
    }

    /// <summary>
    /// 恢复所有Fault集合的重试任务，为每个集合安排Hangfire批处理重试
    /// </summary>
    private async Task RecoverPendingRetryTasksAsync(CancellationToken cancellationToken = default)
    {
        // 恢复所有Fault集合的重试任务
        logger.LogInformation("Starting to recover pending retry tasks for all Fault collections");

        try
        {
            var dbContextType = typeof(TDbContext);
            // 获取DbContext类型对应的IFaultIsolationStore实例，使用表达式树生成的委托
            var faultStoreFactory = CreateFaultStoreFactory(dbContextType);
            var faultStore = faultStoreFactory();
            if (faultStore == null)
            {
                logger.LogError("Could not get fault store for DbContext type {DbContextType}", dbContextType.FullName);
                return;
            }

            // 调用GetAllFaultCollectionNamesAsync方法，使用表达式树生成的委托
            var getAllFaultCollectionNamesFactory = CreateGetAllFaultCollectionNamesFactory(faultStore.GetType());
            var faultCollectionNames = await getAllFaultCollectionNamesFactory(faultStore, cancellationToken);

            foreach (var collectionName in faultCollectionNames)
            {
                logger.LogInformation("Processing Fault collection {CollectionName} for DbContext {DbContextType}",
                    collectionName, dbContextType.FullName);

                // 解析集合名称，提取实体类型名称
                // 集合名称格式：{EntityType}_Fault
                var entityTypeName = collectionName.EndsWith("_Fault")
                    ? collectionName.Substring(0, collectionName.Length - "_Fault".Length)
                    : collectionName;

                // 获取实体类型
                if (!_entityTypeMap.TryGetValue(entityTypeName, out var entityType))
                {
                    logger.LogWarning("Could not find entity type for name {EntityTypeName}", entityTypeName);
                    continue;
                }

                logger.LogInformation("Found entity type {EntityType} for collection {CollectionName}",
                    entityType.FullName, collectionName);

                // 直接调用HangfireSchedulerService的非泛型方法，避免使用反射
                try
                {
                    const int batchSize = 100;
                    schedulerService.ScheduleBatchRetry(entityType, dbContextType, batchSize, cancellationToken);
                    logger.LogInformation("Scheduled batch retry for entity type {EntityType} and DbContext type {DbContextType}",
                        entityType.FullName, dbContextType.FullName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to schedule batch retry for entity type {EntityType} and DbContext type {DbContextType}",
                        entityType.FullName, dbContextType.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Fault collections for DbContext type {DbContextType}",
                typeof(TDbContext).FullName);
        }

        logger.LogInformation("Completed recovery of pending retry tasks for all Fault collections");
    }

    /// <summary>
    /// 扫描当前程序集及所有引用程序集，收集所有实体类型和DbContext类型
    /// </summary>
    private void ScanEntityTypes()
    {
        try
        {
            // 获取当前程序集
            var currentAssembly = Assembly.GetExecutingAssembly();

            // 获取所有引用的程序集
            var referencedAssemblies = currentAssembly.GetReferencedAssemblies()
                .Select(Assembly.Load)
                .ToList();

            // 包括当前程序集
            referencedAssemblies.Add(currentAssembly);

            // 扫描所有实体类型（所有class类型）
            foreach (var assembly in referencedAssemblies)
            {
                try
                {
                    var entityTypes = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericType)
                        .ToList();

                    foreach (var entityType in entityTypes)
                    {
                        _entityTypeMap[entityType.Name] = entityType;
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    logger.LogError(ex, "Failed to load types from assembly {AssemblyName}", assembly.FullName);
                }
            }

            logger.LogInformation("Scanned {EntityTypeCount} entity types for DbContext {DbContextType}",
                _entityTypeMap.Count, typeof(TDbContext).FullName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan entity types");
        }
    }

    /// <summary>
    /// 配置定期重复重试任务
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="batchSize">批量大小</param>
    public void ConfigureRecurringRetry<TEntity>(int batchSize = 100) where TEntity : class
    {
        schedulerService.ScheduleBatchRetry<TEntity, TDbContext>(batchSize);
    }

    /// <summary>
    /// 停止服务
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步任务</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping FaultIsolationService");

        // 停止健康检查监控
        healthChecker.StopMonitoring();

        logger.LogInformation("FaultIsolationService stopped successfully");
        return Task.CompletedTask;
    }
}