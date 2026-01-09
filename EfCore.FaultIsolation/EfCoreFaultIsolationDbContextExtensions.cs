using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using EfCore.FaultIsolation.Services;

namespace Microsoft.EntityFrameworkCore;

public static class EfCoreFaultIsolationDbContextExtensions
{
    public static async Task SaveWithFaultIsolationAsync<TEntity, TDbContext>(
        this DbContext dbContext,
        TEntity entity,
        CancellationToken cancellationToken = default
    ) where TEntity : class where TDbContext : DbContext
    {
        var faultIsolationService = dbContext.GetService<EfCoreFaultIsolationService<TDbContext>>();
        await faultIsolationService.SaveSingleAsync<TEntity>(entity, cancellationToken);
    }
    
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