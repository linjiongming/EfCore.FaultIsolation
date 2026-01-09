using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.HealthChecks;
using EfCore.FaultIsolation.Models;
using EfCore.FaultIsolation.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.Services;

public class EfCoreFaultIsolationService(
    IServiceProvider serviceProvider,
    IRetryService retryService,
    HangfireSchedulerService schedulerService,
    ILogger<EfCoreFaultIsolationService> logger
)
{
    private readonly Dictionary<string, Type> _entityTypeMap = new();
    private readonly List<Type> _dbContextTypes = new();
    
    private IFaultIsolationStore<TDbContext> GetFaultStore<TDbContext>() where TDbContext : DbContext
    {
        return serviceProvider.GetRequiredService<IFaultIsolationStore<TDbContext>>();
    }
    
    /// <summary>
    /// 扫描所有实体类型和DbContext类型
    /// </summary>
    private void ScanEntityAndDbContextTypes()
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
            
            // 扫描所有DbContext类型
            foreach (var assembly in referencedAssemblies)
            {
                try
                {
                    var dbContextTypes = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t))
                        .ToList();
                    
                    _dbContextTypes.AddRange(dbContextTypes);
                }
                catch (ReflectionTypeLoadException ex)
                {
                    logger.LogError(ex, "Failed to load DbContext types from assembly {AssemblyName}", assembly.FullName);
                }
            }
            
            logger.LogInformation("Scanned {EntityTypeCount} entity types and {DbContextTypeCount} DbContext types", 
                _entityTypeMap.Count, _dbContextTypes.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan entity and DbContext types");
        }
    }
    
    /// <summary>
    /// 恢复服务状态，扫描实体类型和DbContext类型，并恢复所有Fault集合的重试任务
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Recovering EfCoreFaultIsolationService");
        
        // 扫描所有实体类型和DbContext类型
        ScanEntityAndDbContextTypes();
        
        // 恢复所有Fault集合的重试任务
        logger.LogInformation("Starting to recover pending retry tasks for all Fault collections");
        
        // 遍历所有注册的DbContext类型
        foreach (var dbContextType in _dbContextTypes)
        {
            try
            {
                // 获取DbContext类型对应的IFaultIsolationStore实例
                var getFaultStoreMethod = GetType().GetMethod(nameof(GetFaultStore), BindingFlags.NonPublic | BindingFlags.Instance);
                if (getFaultStoreMethod == null)
                {
                    logger.LogError("Could not find GetFaultStore method");
                    continue;
                }
                
                var genericGetFaultStoreMethod = getFaultStoreMethod.MakeGenericMethod(dbContextType);
                var faultStore = genericGetFaultStoreMethod.Invoke(this, null);
                if (faultStore == null)
                {
                    logger.LogError("Could not get fault store for DbContext type {DbContextType}", dbContextType.FullName);
                    continue;
                }
                
                // 调用GetAllFaultCollectionNamesAsync方法
                var getAllFaultCollectionNamesMethod = faultStore.GetType().GetMethod(nameof(IFaultIsolationStore<DbContext>.GetAllFaultCollectionNamesAsync));
                if (getAllFaultCollectionNamesMethod == null)
                {
                    logger.LogError("Could not find GetAllFaultCollectionNamesAsync method on fault store");
                    continue;
                }
                
                var faultCollectionNamesTask = (Task<IEnumerable<string>>?)getAllFaultCollectionNamesMethod.Invoke(faultStore, new object[] { cancellationToken });
                if (faultCollectionNamesTask == null)
                {
                    logger.LogError("Could not invoke GetAllFaultCollectionNamesAsync method");
                    continue;
                }
                
                var faultCollectionNames = await faultCollectionNamesTask;
                
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
                    dbContextType.FullName);
            }
        }
        
        logger.LogInformation("Completed recovery of pending retry tasks for all Fault collections");
        logger.LogInformation("EfCoreFaultIsolationService recovered successfully");
    }
    
    public async Task SaveBatchAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : class
        where TDbContext : DbContext
    {
        try
        {
            logger.LogInformation("Attempting to save batch to database");
            using var scope = serviceProvider.CreateScope();
            using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
            
            await dbContext.AddRangeAsync(entities, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Batch saved successfully to database");
            
            // 确保没有在成功保存后将数据添加到FaultStore
            logger.LogDebug("Batch save completed, no exceptions thrown");
        }
        catch (Exception ex)
        {
            // 记录完整异常信息
            logger.LogError(ex, "SaveBatchAsync exception: {ExceptionType}", ex.GetType().FullName);
            
            // 记录内部异常信息
            var innerEx = ex.InnerException;
            int innerExCount = 0;
            while (innerEx != null)
            {
                innerExCount++;
                logger.LogError(innerEx, "Inner Exception {Count}: {ExceptionType}", innerExCount, innerEx.GetType().FullName);
                innerEx = innerEx.InnerException;
            }
            
            try
            {
                bool isFatal = await retryService.IsFatalException(ex);
                bool isRetryable = await retryService.IsRetryableException(ex);
                bool isDataError = await retryService.IsDataErrorException(ex);
                
                logger.LogDebug("Fatal: {Fatal}, Retryable: {Retryable}, DataError: {DataError}", isFatal, isRetryable, isDataError);
                
                if (isFatal)
                {
                    logger.LogInformation("Classified as fatal exception, saving to dead letter");
                    // 致命异常，直接保存到死信
                    await SaveBatchToDeadLetterAsync<TEntity, TDbContext>(entities, ex, "Fatal exception detected");
                }
                else if (isRetryable)
                {
                    logger.LogInformation("Classified as retryable exception, saving to fault store");
                    // 场景1：数据库连接失败，整批保存到LiteDB
                    await SaveBatchToFaultStoreAsync<TEntity, TDbContext>(entities, ex);
                }
                else if (isDataError)
                {
                    logger.LogInformation("Classified as data error, using fallback save");
                    // 场景2：单条数据错误，降级为逐条插入
                    await SaveBatchWithFallbackAsync<TEntity, TDbContext>(entities);
                }
                else
                {
                    logger.LogInformation("Classified as unknown error, saving to dead letter");
                    // 其他未知错误，整批保存到死信
                    await SaveBatchToDeadLetterAsync<TEntity, TDbContext>(entities, ex, "Unknown error");
                }
            }
            catch (Exception handlerEx)
            {
                logger.LogError(handlerEx, "Exception handling error: {ExceptionType}", handlerEx.GetType().FullName);
                // 保存到死信
                await SaveBatchToDeadLetterAsync<TEntity, TDbContext>(entities, ex, "Error handling failed");
            }
        }
    }
    
    private async Task SaveBatchToFaultStoreAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, Exception ex, CancellationToken cancellationToken = default)
        where TEntity : class
        where TDbContext : DbContext
    {
        var faultStore = GetFaultStore<TDbContext>();
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
    
    private async Task SaveBatchWithFallbackAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : class
        where TDbContext : DbContext
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
                        
                        var faultStore = GetFaultStore<TDbContext>();
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
                        
                        var faultStore = GetFaultStore<TDbContext>();
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
                        
                        var faultStore = GetFaultStore<TDbContext>();
                        await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                    }
            }
        }
    }
    
    private async Task SaveBatchToDeadLetterAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, Exception ex, string reason, CancellationToken cancellationToken = default)
        where TEntity : class
        where TDbContext : DbContext
    {
        var faultStore = GetFaultStore<TDbContext>();
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
    
    public async Task SaveSingleAsync<TEntity, TDbContext>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class
        where TDbContext : DbContext
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
                    
                    var faultStore = GetFaultStore<TDbContext>();
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
                    
                    var faultStore = GetFaultStore<TDbContext>();
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
                    
                    var faultStore = GetFaultStore<TDbContext>();
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
                    
                    var faultStore = GetFaultStore<TDbContext>();
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
                
                var faultStore = GetFaultStore<TDbContext>();
                await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
            }
        }
    }
    
    public void ConfigureHealthCheckMonitoring<TEntity, TDbContext>()
        where TEntity : class
        where TDbContext : DbContext
    {
        if (serviceProvider.GetService(typeof(IDatabaseHealthChecker)) is IDatabaseHealthChecker healthChecker)
        {
            healthChecker.DatabaseConnected += (sender, args) =>
            {
                // 当数据库连接恢复时，立即触发一次批量重试
                schedulerService.TriggerImmediateBatchRetry<TEntity, TDbContext>();
            };
            
            healthChecker.StartMonitoring(30);
        }
    }
    
    public void ConfigureRecurringRetry<TEntity, TDbContext>(int batchSize = 100) where TEntity : class where TDbContext : DbContext
    {
        schedulerService.ScheduleBatchRetry<TEntity, TDbContext>(batchSize);
    }
}