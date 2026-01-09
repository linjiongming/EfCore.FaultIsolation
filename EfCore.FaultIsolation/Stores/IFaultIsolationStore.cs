using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.Models;

namespace EfCore.FaultIsolation.Stores;

/// <summary>
/// 故障隔离存储接口，用于管理故障模型和死信队列
/// </summary>
/// <typeparam name="TDbContext">Entity Framework Core 数据库上下文类型</typeparam>
public interface IFaultIsolationStore<TDbContext> where TDbContext : DbContext
{
    /// <summary>
    /// 保存故障模型
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="fault">故障模型实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    ValueTask SaveFaultAsync<TEntity>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) where TEntity : class;
    
    /// <summary>
    /// 获取待处理的故障模型列表
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="batchSize">批量大小，默认值为100</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>待处理的故障模型列表</returns>
    ValueTask<IEnumerable<FaultModel<TEntity>>> GetPendingFaultsAsync<TEntity>(int batchSize = 100, CancellationToken cancellationToken = default) where TEntity : class;
    
    /// <summary>
    /// 删除指定ID的故障模型
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="id">故障模型ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    ValueTask DeleteFaultAsync<TEntity>(Guid id, CancellationToken cancellationToken = default) where TEntity : class;
    
    /// <summary>
    /// 更新故障模型
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="fault">故障模型实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    ValueTask UpdateFaultAsync<TEntity>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) where TEntity : class;
    
    /// <summary>
    /// 保存死信队列
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="deadLetter">死信队列实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    ValueTask SaveDeadLetterAsync<TEntity>(DeadLetter<TEntity> deadLetter, CancellationToken cancellationToken = default) where TEntity : class;
    
    /// <summary>
    /// 获取待处理故障的数量
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>待处理故障的数量</returns>
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
