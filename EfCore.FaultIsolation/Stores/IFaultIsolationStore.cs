using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.Models;

namespace EfCore.FaultIsolation.Stores;

public interface IFaultIsolationStore<TDbContext> where TDbContext : DbContext
{
    ValueTask SaveFaultAsync<TEntity>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) where TEntity : class;
    
    ValueTask<IEnumerable<FaultModel<TEntity>>> GetPendingFaultsAsync<TEntity>(int batchSize = 100, CancellationToken cancellationToken = default) where TEntity : class;
    
    ValueTask DeleteFaultAsync<TEntity>(Guid id, CancellationToken cancellationToken = default) where TEntity : class;
    
    ValueTask UpdateFaultAsync<TEntity>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) where TEntity : class;
    
    ValueTask SaveDeadLetterAsync<TEntity>(DeadLetter<TEntity> deadLetter, CancellationToken cancellationToken = default) where TEntity : class;
    
    ValueTask<int> GetPendingFaultCountAsync<TEntity>(CancellationToken cancellationToken = default) where TEntity : class;
    
    /// <summary>
    /// 获取所有Fault集合的名称
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>所有Fault集合的名称列表</returns>
    ValueTask<IEnumerable<string>> GetAllFaultCollectionNamesAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 根据集合名称获取指定集合的待处理故障
    /// </summary>
    /// <param name="collectionName">集合名称</param>
    /// <param name="batchSize">批量大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>待处理故障列表</returns>
    ValueTask<IEnumerable<object>> GetPendingFaultsByCollectionNameAsync(string collectionName, int batchSize = 100, CancellationToken cancellationToken = default);
}
