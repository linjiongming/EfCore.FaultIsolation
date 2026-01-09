using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.Models;
using EfCore.FaultIsolation.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.FaultIsolation.Services;

/// <summary>
/// 重试作业服务类，用于处理单个和批量故障项的重试逻辑
/// </summary>
/// <param name="retryService">重试服务实例</param>
/// <param name="serviceProvider">服务提供程序实例</param>
public class RetryJobService(
    IRetryService retryService,
    IServiceProvider serviceProvider)
{
    private IFaultIsolationStore<TDbContext> GetFaultStore<TDbContext>() where TDbContext : DbContext
    {
        return serviceProvider.GetRequiredService<IFaultIsolationStore<TDbContext>>();
    }
    
    /// <summary>
    /// 重试单个故障项
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="faultId">故障项唯一标识符</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    public async Task RetryJobAsync<TEntity, TDbContext>(Guid faultId, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        var faultStore = GetFaultStore<TDbContext>();
        var pendingFaults = await faultStore.GetPendingFaultsAsync<TEntity>(1, cancellationToken);
        var fault = pendingFaults.FirstOrDefault(f => f.Id == faultId);
        
        if (fault is null)
            return;
        
        try
        {
            await retryService.RetrySingleAsync<TEntity, TDbContext>(fault.Data, cancellationToken);
            await faultStore.DeleteFaultAsync<TEntity>(fault.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            fault.RetryCount++;
            fault.LastRetryTime = DateTime.UtcNow;
            fault.ErrorMessage = ex.Message;
            
            if (fault.RetryCount >= retryService.GetMaxRetries() || await retryService.IsDataErrorException(ex))
            {
                // 重试超过最大次数或数据错误，转移到死信
                var deadLetter = new DeadLetter<TEntity>
                {
                    Data = fault.Data,
                    TotalRetryCount = fault.RetryCount,
                    Timestamp = fault.Timestamp,
                    LastRetryTime = fault.LastRetryTime,
                    ErrorMessage = fault.ErrorMessage,
                    FailureReason = fault.RetryCount >= retryService.GetMaxRetries() ? "Max retries exceeded" : "Data validation error"
                };
                
                await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                        await faultStore.DeleteFaultAsync<TEntity>(fault.Id, cancellationToken);
            }
            else
            {
                // 计算指数退避时间
                var delay = retryService.CalculateExponentialBackoff(fault.RetryCount);
                fault.NextRetryTime = DateTime.UtcNow.Add(delay);
                
                await faultStore.UpdateFaultAsync(fault, cancellationToken);
                
                // 通过服务提供程序获取HangfireSchedulerService实例，避免循环依赖
                using var scope = serviceProvider.CreateScope();
                var schedulerService = scope.ServiceProvider.GetRequiredService<HangfireSchedulerService>();
                schedulerService.ScheduleRetry<TEntity, TDbContext>(fault.Id, fault.RetryCount, delay);
            }
        }
    }
    
    /// <summary>
    /// 批量重试故障项
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="batchSize">批量大小，默认值为100</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    public async Task BatchRetryJobAsync<TEntity, TDbContext>(int batchSize = 100, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        var faultStore = GetFaultStore<TDbContext>();
        var pendingFaults = await faultStore.GetPendingFaultsAsync<TEntity>(batchSize, cancellationToken);
        
        if (!pendingFaults.Any())
            return;
        
        try
        {
            // 尝试批量重试
            await retryService.RetryBatchAsync<TEntity, TDbContext>(pendingFaults.Select(f => f.Data), cancellationToken);
            
            // 全部成功，删除所有重试记录
            foreach (var fault in pendingFaults)
            {
                await faultStore.DeleteFaultAsync<TEntity>(fault.Id, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (await retryService.IsRetryableException(ex))
            {
                // 批量重试失败是因为数据库连接问题，更新所有故障记录的重试次数和下次重试时间
                foreach (var fault in pendingFaults)
                {
                    fault.RetryCount++;
                    fault.LastRetryTime = DateTime.UtcNow;
                    fault.ErrorMessage = ex.Message;
                    
                    if (fault.RetryCount >= retryService.GetMaxRetries())
                    {
                        // 超过最大重试次数，转移到死信
                        var deadLetter = new DeadLetter<TEntity>
                        {
                            Data = fault.Data,
                            TotalRetryCount = fault.RetryCount,
                            Timestamp = fault.Timestamp,
                            LastRetryTime = fault.LastRetryTime,
                            ErrorMessage = fault.ErrorMessage,
                            FailureReason = "Max retries exceeded"
                        };
                        
                        await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                        await faultStore.DeleteFaultAsync<TEntity>(fault.Id, cancellationToken);
                    }
                    else
                    {
                        var delay = retryService.CalculateExponentialBackoff(fault.RetryCount);
                        fault.NextRetryTime = DateTime.UtcNow.Add(delay);
                        
                        await faultStore.UpdateFaultAsync(fault, cancellationToken);
                    }
                }
            }
            else if (await retryService.IsDataErrorException(ex))
            {
                // 批量重试失败是因为数据错误，降级为逐条重试
                foreach (var fault in pendingFaults)
                {
                    await RetrySingleWithDataErrorHandlingAsync<TEntity, TDbContext>(fault, cancellationToken);
                }
            }
        }
    }
    
    /// <summary>
    /// 基于类型的批量重试方法（非泛型）
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="dbContextType">数据库上下文类型</param>
    /// <param name="batchSize">批量大小，默认值为100</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    public async Task BatchRetryJobAsync(Type entityType, Type dbContextType, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        // 获取实体类型和DbContext类型的泛型BatchRetryJobAsync方法
        var method = typeof(RetryJobService).GetMethod(nameof(BatchRetryJobAsync), new[] { typeof(int) });
        if (method == null)
        {
            throw new InvalidOperationException($"Could not find BatchRetryJobAsync method with int parameter");
        }
        
        // 构造泛型方法
        var genericMethod = method.MakeGenericMethod(entityType, dbContextType);
        
        // 调用泛型方法
        var taskResult = genericMethod.Invoke(this, new object[] { batchSize, cancellationToken });
        if (taskResult is Task task)
        {
            await task;
        }
        else
        {
            throw new InvalidOperationException($"Expected Task result from BatchRetryJobAsync invocation, got {taskResult?.GetType().Name}");
        }
    }
    
    private async Task RetrySingleWithDataErrorHandlingAsync<TEntity, TDbContext>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        var faultStore = GetFaultStore<TDbContext>();
        try
        {
            await retryService.RetrySingleAsync<TEntity, TDbContext>(fault.Data, cancellationToken);
            await faultStore.DeleteFaultAsync<TEntity>(fault.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            fault.RetryCount++;
            fault.LastRetryTime = DateTime.UtcNow;
            fault.ErrorMessage = ex.Message;
            
            if (await retryService.IsDataErrorException(ex))
            {
                // 单条数据错误，直接转移到死信
                var deadLetter = new DeadLetter<TEntity>
                {
                    Data = fault.Data,
                    TotalRetryCount = fault.RetryCount,
                    Timestamp = fault.Timestamp,
                    LastRetryTime = fault.LastRetryTime,
                    ErrorMessage = fault.ErrorMessage,
                    FailureReason = "Data validation error"
                };
                
                await faultStore.SaveDeadLetterAsync(deadLetter, cancellationToken);
                await faultStore.DeleteFaultAsync<TEntity>(fault.Id, cancellationToken);
            }
            else if (fault.RetryCount >= retryService.GetMaxRetries())
            {
                // 超过最大重试次数，转移到死信
                var deadLetter = new DeadLetter<TEntity>
                {
                    Data = fault.Data,
                    TotalRetryCount = fault.RetryCount,
                    Timestamp = fault.Timestamp,
                    LastRetryTime = fault.LastRetryTime,
                    ErrorMessage = fault.ErrorMessage,
                    FailureReason = "Max retries exceeded"
                };
                
                await faultStore.SaveDeadLetterAsync(deadLetter);
                await faultStore.DeleteFaultAsync<TEntity>(fault.Id);
            }
            else
            {
                // 其他临时错误，继续重试
                var delay = retryService.CalculateExponentialBackoff(fault.RetryCount);
                fault.NextRetryTime = DateTime.UtcNow.Add(delay);
                
                await faultStore.UpdateFaultAsync(fault, cancellationToken);
            }
        }
    }
    

}
