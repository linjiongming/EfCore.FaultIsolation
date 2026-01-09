using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using EfCore.FaultIsolation.Services;

namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// DbContext 扩展方法，用于支持故障隔离的保存操作
/// </summary>
public static class EfCoreFaultIsolationDbContextExtensions
{
    /// <summary>
    /// 使用故障隔离机制保存单个实体
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
    /// <param name="dbContext">数据库上下文实例</param>
    /// <param name="entity">要保存的实体</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    public static async Task SaveWithFaultIsolationAsync<TEntity, TDbContext>(
        this DbContext dbContext,
        TEntity entity,
        CancellationToken cancellationToken = default
    ) where TEntity : class where TDbContext : DbContext
    {
        var faultIsolationService = dbContext.GetService<EfCoreFaultIsolationService<TDbContext>>();
        await faultIsolationService.SaveSingleAsync<TEntity>(entity, cancellationToken);
    }
    
    /// <summary>
    /// 使用故障隔离机制保存多个实体
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
    /// <param name="dbContext">数据库上下文实例</param>
    /// <param name="entities">要保存的实体集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    public static async Task SaveRangeWithFaultIsolationAsync<TEntity, TDbContext>(
        this DbContext dbContext,
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default
    ) where TEntity : class where TDbContext : DbContext
    {
        var faultIsolationService = dbContext.GetService<EfCoreFaultIsolationService<TDbContext>>();
        await faultIsolationService.SaveBatchAsync<TEntity>(entities, cancellationToken);
    }
}