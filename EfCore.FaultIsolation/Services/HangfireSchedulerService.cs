using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
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
            // 直接创建RetryJobService实例，避免Hangfire的动态类型创建问题
            using var scope = serviceProvider.CreateScope();
            var retryJobService = scope.ServiceProvider.GetRequiredService<RetryJobService>();
            
            // 立即执行重试，而不是通过Hangfire调度
            // 这避免了Hangfire的序列化和激活问题
            Task.Run(async () => 
            {
                await Task.Delay(delay);
                await retryJobService.RetryJobAsync<TEntity, TDbContext>(faultId, cancellationToken);
            });
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
            // 直接创建RetryJobService实例，避免Hangfire的动态类型创建问题
            using var scope = serviceProvider.CreateScope();
            var retryJobService = scope.ServiceProvider.GetRequiredService<RetryJobService>();
            
            // 立即执行批量重试，而不是通过Hangfire调度
            Task.Run(async () => 
            {
                await retryJobService.BatchRetryJobAsync<TEntity, TDbContext>(batchSize, cancellationToken);
            });
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
            // 直接创建RetryJobService实例，避免Hangfire的动态类型创建问题
            using var scope = serviceProvider.CreateScope();
            var retryJobService = scope.ServiceProvider.GetRequiredService<RetryJobService>();
            
            // 立即执行批量重试，而不是通过Hangfire调度
            Task.Run(async () => 
            {
                await retryJobService.BatchRetryJobAsync(entityType, dbContextType, batchSize, cancellationToken);
            });
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
            // 直接创建RetryJobService实例，避免Hangfire的动态类型创建问题
            using var scope = serviceProvider.CreateScope();
            var retryJobService = scope.ServiceProvider.GetRequiredService<RetryJobService>();
            
            // 立即执行批量重试，而不是通过Hangfire调度
            Task.Run(async () => 
            {
                await retryJobService.BatchRetryJobAsync<TEntity, TDbContext>(batchSize, cancellationToken);
            });
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
