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

public class HangfireSchedulerService(IServiceProvider serviceProvider, ILogger<HangfireSchedulerService> logger)
{
    public void ConfigureHangfire(string sqliteConnectionString = "hangfire.db")
    {
        // 配置Hangfire使用服务提供程序激活器
        GlobalConfiguration.Configuration
            .UseActivator(new HangfireServiceProviderActivator(serviceProvider, logger))
            .UseSQLiteStorage(sqliteConnectionString);
    }
    
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
