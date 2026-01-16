using EfCore.FaultIsolation.Models;
using EfCore.FaultIsolation.Services;
using EfCore.FaultIsolation.Stores;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.Interceptors;

/// <summary>
/// EF Core故障隔离拦截器，用于在SaveChanges时自动进行故障隔离处理
/// </summary>
public class FaultIsolationInterceptor(
    IServiceProvider serviceProvider,
    ILogger<FaultIsolationInterceptor> logger) : SaveChangesInterceptor
{
    /// <summary>
    /// 在异步SaveChanges完成后拦截，处理故障
    /// </summary>
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// 在异步SaveChanges执行过程中拦截，捕获异常
    /// </summary>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
        catch (Exception ex)
        {
            if (eventData.Context is not null)
            {
                try
                {
                    // 捕获所有需要保存的实体
                    var entitiesToSave = eventData.Context.ChangeTracker.Entries()
                        .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                        .ToList();

                    if (entitiesToSave.Count > 0)
                    {
                        // 记录异常信息
                        logger.LogError(ex, "SaveChanges failed, attempting fault isolation for {EntityCount} entities", entitiesToSave.Count);

                        // 直接调用非泛型方法处理故障隔离
                        await HandleFaultIsolationAsync(eventData.Context, entitiesToSave, ex, cancellationToken);
                    }
                }
                catch (Exception innerEx)
                {
                    logger.LogError(innerEx, "Fault isolation handling failed");
                }
            }

            // 重新抛出原始异常
            throw;
        }
    }

    /// <summary>
    /// 在异步SaveChanges失败时拦截，执行故障隔离逻辑
    /// </summary>
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is null)
        {
            await base.SaveChangesFailedAsync(eventData, cancellationToken);
            return;
        }

        try
        {
            // 捕获所有需要保存的实体
            var entitiesToSave = eventData.Context.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();

            if (entitiesToSave.Count == 0)
            {
                await base.SaveChangesFailedAsync(eventData, cancellationToken);
                return;
            }

            // 记录异常信息
            logger.LogError(eventData.Exception, "SaveChanges failed, attempting fault isolation for {EntityCount} entities", entitiesToSave.Count);

            // 直接调用非泛型方法处理故障隔离
            await HandleFaultIsolationAsync(eventData.Context, entitiesToSave, eventData.Exception, cancellationToken);

            // 不调用base方法，因为我们已经处理了异常
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fault isolation handling failed");
            await base.SaveChangesFailedAsync(eventData, cancellationToken);
            return;
        }
    }

    /// <summary>
    /// 非泛型的故障隔离处理方法，用于在拦截器中直接调用
    /// </summary>
    private async Task HandleFaultIsolationAsync(DbContext dbContext, List<EntityEntry> entries, Exception ex, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();

        // 获取必要的服务
        var retryService = scope.ServiceProvider.GetRequiredService<IRetryService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<FaultIsolationInterceptor>>();

        try
        {
            bool isRetryable = retryService.IsRetryableException(ex);
            bool isDataError = retryService.IsDataErrorException(ex);

            logger.LogDebug("Exception classification: Retryable={Retryable}, DataError={DataError}",
                isRetryable, isDataError);

            if (isDataError)
            {
                logger.LogInformation("Exception is data error, saving to dead letter queue");
                await SaveToDeadLetterAsync(dbContext, entries, ex, "Data error detected", cancellationToken);
            }
            else if (isRetryable)
            {
                logger.LogInformation("Exception is retryable, saving to fault store");
                await SaveToFaultStoreAsync(dbContext, entries, ex, cancellationToken);
            }
            else
            {
                logger.LogInformation("Exception is unknown, saving to fault store");
                await SaveToFaultStoreAsync(dbContext, entries, ex, cancellationToken);
            }
        }
        catch (Exception handlerEx)
        {
            logger.LogError(handlerEx, "Error handling fault isolation");
            await SaveToDeadLetterAsync(dbContext, entries, ex, "Error handling failed", cancellationToken);
        }
    }

    /// <summary>
    /// 将实体保存到故障存储
    /// </summary>
    private async Task SaveToFaultStoreAsync(DbContext dbContext, List<EntityEntry> entries, Exception ex, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var faultStore = scope.ServiceProvider.GetRequiredService(typeof(IFaultIsolationStore<>).MakeGenericType(dbContext.GetType()));

        foreach (var entry in entries)
        {
            try
            {
                // 获取实体类型
                var entityType = entry.Entity.GetType();

                // 创建FaultModel并保存（使用反射）
                var faultType = typeof(FaultModel<>).MakeGenericType(entityType);
                var fault = Activator.CreateInstance(faultType);

                // 设置属性
                faultType.GetProperty("Data")?.SetValue(fault, entry.Entity);
                faultType.GetProperty("RetryCount")?.SetValue(fault, 0);
                faultType.GetProperty("Timestamp")?.SetValue(fault, DateTime.UtcNow);
                faultType.GetProperty("LastRetryTime")?.SetValue(fault, null);
                faultType.GetProperty("NextRetryTime")?.SetValue(fault, DateTime.UtcNow);
                faultType.GetProperty("ErrorMessage")?.SetValue(fault, ex.Message);
                faultType.GetProperty("Type")?.SetValue(fault, entry.State);

                // 调用SaveFaultAsync方法
                var saveFaultMethod = faultStore.GetType().GetMethod("SaveFaultAsync");
                if (saveFaultMethod != null)
                {
                    var genericMethod = saveFaultMethod.MakeGenericMethod(entityType);
                    await (Task)genericMethod.Invoke(faultStore, [fault, cancellationToken])!;
                }

                logger.LogInformation("Saved entity of type {EntityType} to fault store", entityType.FullName);
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx, "Failed to save fault for entity type {EntityType}", entry.Entity.GetType().FullName);
            }
        }
    }

    /// <summary>
    /// 将实体保存到死信队列
    /// </summary>
    private async Task SaveToDeadLetterAsync(DbContext dbContext, List<EntityEntry> entries, Exception ex, string reason, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var faultStore = scope.ServiceProvider.GetRequiredService(typeof(IFaultIsolationStore<>).MakeGenericType(dbContext.GetType()));

        foreach (var entry in entries)
        {
            try
            {
                // 获取实体类型
                var entityType = entry.Entity.GetType();

                // 创建DeadLetter并保存（使用反射）
                var deadLetterType = typeof(DeadLetter<>).MakeGenericType(entityType);
                var deadLetter = Activator.CreateInstance(deadLetterType);

                // 设置属性
                deadLetterType.GetProperty("Data")?.SetValue(deadLetter, entry.Entity);
                deadLetterType.GetProperty("TotalRetryCount")?.SetValue(deadLetter, 0);
                deadLetterType.GetProperty("Timestamp")?.SetValue(deadLetter, DateTime.UtcNow);
                deadLetterType.GetProperty("LastRetryTime")?.SetValue(deadLetter, null);
                deadLetterType.GetProperty("ErrorMessage")?.SetValue(deadLetter, ex.Message);
                deadLetterType.GetProperty("FailureReason")?.SetValue(deadLetter, reason);
                deadLetterType.GetProperty("Type")?.SetValue(deadLetter, entry.State);

                // 调用SaveDeadLetterAsync方法
                var saveDeadLetterMethod = faultStore.GetType().GetMethod("SaveDeadLetterAsync");
                if (saveDeadLetterMethod != null)
                {
                    var genericMethod = saveDeadLetterMethod.MakeGenericMethod(entityType);
                    await (Task)genericMethod.Invoke(faultStore, [deadLetter, cancellationToken])!;
                }

                logger.LogInformation("Saved entity of type {EntityType} to dead letter queue", entityType.FullName);
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx, "Failed to save dead letter for entity type {EntityType}", entry.Entity.GetType().FullName);
            }
        }
    }
}