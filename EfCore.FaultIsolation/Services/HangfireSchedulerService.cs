using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage.SQLite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EfCore.FaultIsolation.Services;

/// <summary>
/// Hangfire调度服务，用于管理重试任务的调度
/// </summary>
public class HangfireSchedulerService(IServiceProvider serviceProvider, ILogger<HangfireSchedulerService> logger)
{
    /// <summary>
    /// 配置Hangfire使用SQLite存储
    /// </summary>
    /// <param name="sqliteConnectionString">SQLite连接字符串</param>
    public void ConfigureHangfire(string sqliteConnectionString = "hangfire.db")
    {
        // 配置Hangfire使用服务提供程序激活器
        GlobalConfiguration.Configuration
            .UseActivator(new HangfireServiceProviderActivator(serviceProvider, logger))
            .UseSQLiteStorage(sqliteConnectionString);
    }
    
    /// <summary>
    /// 调度单个实体的重试任务
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="faultId">故障记录ID</param>
    /// <param name="retryCount">当前重试次数</param>
    /// <param name="delay">延迟时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void ScheduleRetry<TEntity, TDbContext>(Guid faultId, int retryCount, TimeSpan delay, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        try
        {
            // 使用Hangfire调度重试任务
            var jobId = BackgroundJob.Schedule<RetryJobService>(
                job => job.RetryJobAsync<TEntity, TDbContext>(faultId, cancellationToken),
                delay);
            
            // 添加英文描述
            JobStorage.Current.GetConnection().SetJobParameter(jobId, "Description", 
                $"Retry job for {typeof(TEntity).Name} entity (ID: {faultId}), retry attempt #{retryCount+1}");
        }
        catch (Exception ex)
        {
            // 记录异常但不影响主流程
            logger.LogError(ex, "Retry scheduling error: {Message}", ex.Message);
        }
    }
    
    /// <summary>
    /// 调度批量实体的重试任务
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="batchSize">批量大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void ScheduleBatchRetry<TEntity, TDbContext>(int batchSize = 100, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        try
        {
            // 使用Hangfire调度批量重试任务
            var jobId = BackgroundJob.Enqueue<RetryJobService>(
                job => job.BatchRetryJobAsync<TEntity, TDbContext>(batchSize, cancellationToken));
            
            // 添加英文描述
            JobStorage.Current.GetConnection().SetJobParameter(jobId, "Description", 
                $"Batch retry job for {typeof(TEntity).Name} entities, batch size: {batchSize}");
        }
        catch (Exception ex)
        {
            // 记录异常但不影响主流程
            logger.LogError(ex, "Batch retry scheduling error: {Message}", ex.Message);
        }
    }
    
    /// <summary>
    /// 非泛型方法：调度批量实体的重试任务
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="dbContextType">数据库上下文类型</param>
    /// <param name="batchSize">批量大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void ScheduleBatchRetry(Type entityType, Type dbContextType, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            // 使用Hangfire调度非泛型批量重试任务
            // 使用反射创建泛型方法调用
            var method = typeof(HangfireSchedulerService)
                .GetMethod(nameof(ScheduleBatchRetry), new Type[] { typeof(int), typeof(CancellationToken) })
                ?.MakeGenericMethod(entityType, dbContextType);
            
            if (method != null)
            {
                method.Invoke(this, new object[] { batchSize, cancellationToken });
            }
        }
        catch (Exception ex)
        {
            // 记录异常但不影响主流程
            logger.LogError(ex, "Batch retry scheduling error: {Message}", ex.Message);
        }
    }
    
    /// <summary>
    /// 立即触发批量实体的重试任务
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="batchSize">批量大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void TriggerImmediateBatchRetry<TEntity, TDbContext>(int batchSize = 100, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        try
        {
            // 使用Hangfire立即执行批量重试任务
            var jobId = BackgroundJob.Enqueue<RetryJobService>(
                job => job.BatchRetryJobAsync<TEntity, TDbContext>(batchSize, cancellationToken));
            
            // 添加英文描述
            JobStorage.Current.GetConnection().SetJobParameter(jobId, "Description", 
                $"Immediate batch retry job for {typeof(TEntity).Name} entities, triggered by database connection recovery");
        }
        catch (Exception ex)
        {
            // 记录异常但不影响主流程
            logger.LogError(ex, "Immediate batch retry error: {Message}", ex.Message);
        }
    }
    
    /// <summary>
    /// 非泛型方法：立即触发批量实体的重试任务
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="dbContextType">数据库上下文类型</param>
    /// <param name="batchSize">批量大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void TriggerImmediateBatchRetry(Type entityType, Type dbContextType, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            // 使用反射获取泛型TriggerImmediateBatchRetry方法
            var method = typeof(HangfireSchedulerService)
                .GetMethod(nameof(TriggerImmediateBatchRetry), new Type[] { typeof(int), typeof(CancellationToken) })
                ?.MakeGenericMethod(entityType, dbContextType);
            
            if (method != null)
            {
                method.Invoke(this, new object[] { batchSize, cancellationToken });
            }
        }
        catch (Exception ex)
        {
            // 记录异常但不影响主流程
            logger.LogError(ex, "Immediate batch retry error: {Message}", ex.Message);
        }
    }
    
    private class HangfireServiceProviderActivator(IServiceProvider serviceProvider, ILogger<HangfireSchedulerService> logger) : JobActivator
    {
        public override object ActivateJob(Type jobType)
        {
            try
            {
                // 尝试从服务提供程序创建实例
                return serviceProvider.GetRequiredService(jobType);
            }
            catch (Exception ex)
            {
                // 记录异常但不影响主流程
                logger.LogError(ex, "Hangfire job activation error for type {JobType}: {Message}", jobType.FullName, ex.Message);
                throw;
            }
        }
    }
}
