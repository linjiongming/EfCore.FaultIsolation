using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.HealthChecks;
using EfCore.FaultIsolation.Models;
using EfCore.FaultIsolation.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.Services;

public class EfCoreFaultIsolationService<TDbContext>(
    IServiceProvider serviceProvider,
    IRetryService retryService,
    HangfireSchedulerService schedulerService,
    ILogger<EfCoreFaultIsolationService<TDbContext>> logger,
    IDatabaseHealthChecker<TDbContext> healthChecker
) where TDbContext : DbContext
{
    private readonly Dictionary<string, Type> _entityTypeMap = new();

    
    // 缓存用于获取FaultStore的委托，避免重复使用反射
    private readonly Dictionary<Type, Func<object>> _faultStoreFactories = new();
    
    // 缓存用于获取所有Fault集合名称的委托
    private readonly Dictionary<Type, Func<object, CancellationToken, Task<IEnumerable<string>>>> _getAllFaultCollectionNamesFactories = new();
    
    private readonly IDatabaseHealthChecker<TDbContext> _healthChecker = healthChecker;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRetryService _retryService = retryService;
    private readonly HangfireSchedulerService _schedulerService = schedulerService;
    private readonly ILogger<EfCoreFaultIsolationService<TDbContext>> _logger = logger;

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

        // 使用表达式树生成委托
        var getFaultStoreMethod = GetType().GetMethod(nameof(GetFaultStore), BindingFlags.NonPublic | BindingFlags.Instance);
        if (getFaultStoreMethod == null)
        {
            logger.LogError("Could not find GetFaultStore method");
            return () => null;
        }

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
        var getAllFaultCollectionNamesMethod = faultStoreType.GetMethod(nameof(IFaultIsolationStore<DbContext>.GetAllFaultCollectionNamesAsync));
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
            _healthChecker.DatabaseConnected += (sender, args) =>
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
        logger.LogInformation("Starting EfCoreFaultIsolationService");

        // 扫描当前程序集及所有引用程序集，收集所有实体类型
            ScanEntityTypes();

        // 恢复所有Fault集合的重试任务，为每个集合安排Hangfire批处理重试
        await RecoverPendingRetryTasksAsync(cancellationToken);

        // 注册DatabaseConnected事件
        RegisterDatabaseConnectedEvent();

        // 启动健康检查监控
        _healthChecker.StartMonitoring(30);
        logger.LogInformation("Health check monitoring started");

        logger.LogInformation("EfCoreFaultIsolationService started successfully");
    }

    /// <summary>
    /// 恢复所有Fault集合的重试任务，为每个集合安排Hangfire批处理重试
    /// </summary>
    private async Task RecoverPendingRetryTasksAsync(CancellationToken cancellationToken = default)
    {
        // 恢复所有Fault集合的重试任务
        _logger.LogInformation("Starting to recover pending retry tasks for all Fault collections");

        try
        {
            var dbContextType = typeof(TDbContext);
            // 获取DbContext类型对应的IFaultIsolationStore实例，使用表达式树生成的委托
            var faultStoreFactory = CreateFaultStoreFactory(dbContextType);
            var faultStore = faultStoreFactory();
            if (faultStore == null)
            {
                _logger.LogError("Could not get fault store for DbContext type {DbContextType}", dbContextType.FullName);
                return;
            }

            // 调用GetAllFaultCollectionNamesAsync方法，使用表达式树生成的委托
            var getAllFaultCollectionNamesFactory = CreateGetAllFaultCollectionNamesFactory(faultStore.GetType());
            var faultCollectionNames = await getAllFaultCollectionNamesFactory(faultStore, cancellationToken);

            foreach (var collectionName in faultCollectionNames)
            {
                _logger.LogInformation("Processing Fault collection {CollectionName} for DbContext {DbContextType}",
                    collectionName, dbContextType.FullName);

                // 解析集合名称，提取实体类型名称
                // 集合名称格式：{EntityType}_Fault
                var entityTypeName = collectionName.EndsWith("_Fault")
                    ? collectionName.Substring(0, collectionName.Length - "_Fault".Length)
                    : collectionName;

                // 获取实体类型
                if (!_entityTypeMap.TryGetValue(entityTypeName, out var entityType))
                {
                    _logger.LogWarning("Could not find entity type for name {EntityTypeName}", entityTypeName);
                    continue;
                }

                _logger.LogInformation("Found entity type {EntityType} for collection {CollectionName}",
                    entityType.FullName, collectionName);

                // 直接调用HangfireSchedulerService的非泛型方法，避免使用反射
                try
                {
                    const int batchSize = 100;
                    _schedulerService.ScheduleBatchRetry(entityType, dbContextType, batchSize, cancellationToken);
                    _logger.LogInformation("Scheduled batch retry for entity type {EntityType} and DbContext type {DbContextType}",
                        entityType.FullName, dbContextType.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to schedule batch retry for entity type {EntityType} and DbContext type {DbContextType}",
                        entityType.FullName, dbContextType.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Fault collections for DbContext type {DbContextType}",
                typeof(TDbContext).FullName);
        }

        _logger.LogInformation("Completed recovery of pending retry tasks for all Fault collections");
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



    public async Task SaveBatchAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        try
        {
            _logger.LogInformation("Attempting to save batch to database");
            using var scope = _serviceProvider.CreateScope();
            using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            await dbContext.AddRangeAsync(entities, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Batch saved successfully to database");

            // 确保没有在成功保存后将数据添加到FaultStore
            _logger.LogDebug("Batch save completed, no exceptions thrown");
        }
        catch (Exception ex)
        {
            // 记录完整异常信息
            _logger.LogError(ex, "SaveBatchAsync exception: {ExceptionType}", ex.GetType().FullName);

            // 记录内部异常信息
            var innerEx = ex.InnerException;
            int innerExCount = 0;
            while (innerEx != null)
            {
                innerExCount++;
                _logger.LogError(innerEx, "Inner Exception {Count}: {ExceptionType}", innerExCount, innerEx.GetType().FullName);
                innerEx = innerEx.InnerException;
            }

            try
            {
                bool isFatal = await _retryService.IsFatalException(ex);
                bool isRetryable = await _retryService.IsRetryableException(ex);
                bool isDataError = await _retryService.IsDataErrorException(ex);

                _logger.LogDebug("Fatal: {Fatal}, Retryable: {Retryable}, DataError: {DataError}", isFatal, isRetryable, isDataError);

                if (isFatal)
                {
                    _logger.LogInformation("Classified as fatal exception, saving to dead letter");
                    // 致命异常，直接保存到死信
                    await SaveBatchToDeadLetterAsync<TEntity>(entities, ex, "Fatal exception detected");
                }
                else if (isRetryable)
                {
                    _logger.LogInformation("Classified as retryable exception, saving to fault store");
                    // 场景1：数据库连接失败，整批保存到LiteDB
                    await SaveBatchToFaultStoreAsync<TEntity>(entities, ex);
                }
                else if (isDataError)
                {
                    _logger.LogInformation("Classified as data error, using fallback save");
                    // 场景2：单条数据错误，降级为逐条插入
                    await SaveBatchWithFallbackAsync<TEntity>(entities);
                }
                else
                {
                    _logger.LogInformation("Classified as unknown error, saving to dead letter");
                    // 其他未知错误，整批保存到LiteDB
                    await SaveBatchToFaultStoreAsync<TEntity>(entities, ex);
                }
            }
            catch (Exception handlerEx)
            {
                _logger.LogError(handlerEx, "Exception handling error: {ExceptionType}", handlerEx.GetType().FullName);
                // 保存到死信
                await SaveBatchToDeadLetterAsync<TEntity>(entities, ex, "Error handling failed");
            }
        }
    }

    private async Task SaveBatchToFaultStoreAsync<TEntity>(IEnumerable<TEntity> entities, Exception ex, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var faultStore = GetFaultStore();
        logger.LogInformation("Saving batch to fault store");
        foreach (var entity in entities)
        {
            var fault = new FaultModel<TEntity>
            {
                Data = entity,
                RetryCount = 0,
                Timestamp = DateTime.UtcNow,
                LastRetryTime = null,
                NextRetryTime = DateTime.UtcNow,
                ErrorMessage = ex.Message
            };

            await faultStore.SaveFaultAsync(fault, cancellationToken);
        }
        logger.LogInformation("Batch saved to fault store successfully");
    }

    private async Task SaveBatchWithFallbackAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        logger.LogInformation("Using fallback save for batch");
        foreach (var entity in entities)
        {
            try
            {
                logger.LogInformation("Attempting to save single entity: {EntityType}", entity.GetType().Name);
                using var scope = serviceProvider.CreateScope();
                using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

                await dbContext.AddAsync(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Single entity saved successfully");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Single entity save failed: {Message}", ex.Message);
                bool isFatal = await retryService.IsFatalException(ex);
                bool isRetryable = await retryService.IsRetryableException(ex);
                bool isDataError = await retryService.IsDataErrorException(ex);

                logger.LogDebug("Single entity - Fatal: {Fatal}, Retryable: {Retryable}, DataError: {DataError}", isFatal, isRetryable, isDataError);

                if (isFatal)
                {
                    logger.LogInformation("Single entity error is fatal, saving to dead letter");
                    // 致命异常，保存到死信
                    var deadLetter = new DeadLetter<TEntity>
                    {
                        Data = entity,
                        TotalRetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        ErrorMessage = ex.Message,
                        FailureReason = "Fatal exception detected"
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                }
                else if (isRetryable)
                {
                    logger.LogInformation("Single entity error is retryable, saving to fault store");
                    // 单条数据的连接错误，保存到故障存储
                    var fault = new FaultModel<TEntity>
                    {
                        Data = entity,
                        RetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        NextRetryTime = DateTime.UtcNow,
                        ErrorMessage = ex.Message
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveFaultAsync(fault, cancellationToken);
                }
                else if (isDataError)
                {
                    logger.LogInformation("Single entity error is data error, saving to dead letter");
                    // 单条数据的验证错误，保存到死信
                    var deadLetter = new DeadLetter<TEntity>
                    {
                        Data = entity,
                        TotalRetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        ErrorMessage = ex.Message,
                        FailureReason = "Data validation error"
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                }
            }
        }
    }

    private async Task SaveBatchToDeadLetterAsync<TEntity>(IEnumerable<TEntity> entities, Exception ex, string reason, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var faultStore = GetFaultStore();
        logger.LogInformation("Saving batch to dead letter store: {Reason}", reason);
        foreach (var entity in entities)
        {
            var deadLetter = new DeadLetter<TEntity>
            {
                Data = entity,
                TotalRetryCount = 0,
                Timestamp = DateTime.UtcNow,
                LastRetryTime = null,
                ErrorMessage = ex.Message,
                FailureReason = reason
            };

            await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
        }
    }

    public async Task SaveSingleAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        try
        {
            logger.LogInformation("Attempting to save single entity to database");
            using var scope = serviceProvider.CreateScope();
            using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            await dbContext.AddAsync(entity, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Single entity saved successfully to database");

            // 确保没有在成功保存后将数据添加到FaultStore
            logger.LogDebug("Single save completed, no exceptions thrown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SaveSingleAsync exception: {ExceptionType}", ex.GetType().FullName);

            try
            {
                bool isFatal = await retryService.IsFatalException(ex);
                bool isRetryable = await retryService.IsRetryableException(ex);
                bool isDataError = await retryService.IsDataErrorException(ex);

                logger.LogDebug("Fatal: {Fatal}, Retryable: {Retryable}, DataError: {DataError}", isFatal, isRetryable, isDataError);

                if (isFatal)
                {
                    logger.LogInformation("Single save classified as fatal, saving to dead letter");
                    // 致命异常，直接保存到死信
                    var deadLetter = new DeadLetter<TEntity>
                    {
                        Data = entity,
                        TotalRetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        ErrorMessage = ex.Message,
                        FailureReason = "Fatal exception detected"
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                }
                else if (isRetryable)
                {
                    logger.LogInformation("Single save classified as retryable, saving to fault store");
                    // 临时失败：数据库连接问题，保存到故障存储
                    var fault = new FaultModel<TEntity>
                    {
                        Data = entity,
                        RetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        NextRetryTime = DateTime.UtcNow,
                        ErrorMessage = ex.Message
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveFaultAsync(fault, cancellationToken);
                }
                else if (isDataError)
                {
                    logger.LogInformation("Single save classified as data error, saving to dead letter");
                    // 永久失败：数据本身错误，保存到死信
                    var deadLetter = new DeadLetter<TEntity>
                    {
                        Data = entity,
                        TotalRetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        ErrorMessage = ex.Message,
                        FailureReason = "Data validation error"
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                }
                else
                {
                    logger.LogInformation("Single save classified as unknown, saving to fault store");
                    // 其他未知错误，先尝试重试几次
                    var fault = new FaultModel<TEntity>
                    {
                        Data = entity,
                        RetryCount = 0,
                        Timestamp = DateTime.UtcNow,
                        LastRetryTime = null,
                        NextRetryTime = DateTime.UtcNow,
                        ErrorMessage = ex.Message
                    };

                    var faultStore = GetFaultStore();
                    await faultStore.SaveFaultAsync(fault, cancellationToken);
                }
            }
            catch (Exception handlerEx)
            {
                logger.LogError(handlerEx, "Exception handling error: {ExceptionType}", handlerEx.GetType().FullName);
                // 保存到死信
                var deadLetter = new DeadLetter<TEntity>
                {
                    Data = entity,
                    TotalRetryCount = 0,
                    Timestamp = DateTime.UtcNow,
                    LastRetryTime = null,
                    ErrorMessage = ex.Message,
                    FailureReason = "Error handling failed"
                };

                var faultStore = GetFaultStore();
                await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
            }
        }
    }



    public void ConfigureRecurringRetry<TEntity>(int batchSize = 100) where TEntity : class
    {
        schedulerService.ScheduleBatchRetry<TEntity, TDbContext>(batchSize);
    }
}