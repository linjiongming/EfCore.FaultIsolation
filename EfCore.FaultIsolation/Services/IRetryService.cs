namespace EfCore.FaultIsolation.Services;

/// <summary>
/// 重试服务接口，用于管理实体的重试操作和异常分类
/// </summary>
public interface IRetryService
{
    /// <summary>
    /// 获取最大重试次数
    /// </summary>
    /// <returns>最大重试次数</returns>
    int MaxRetries { get; set; }

    /// <summary>
    /// 批量重试实体操作
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="entities">要重试的实体集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    Task RetryBatchAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext;
    
    /// <summary>
    /// 单个实体重试操作
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="entity">要重试的实体</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步操作结果</returns>
    Task RetrySingleAsync<TEntity, TDbContext>(TEntity entity, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext;
    
    /// <summary>
    /// 检查异常是否可重试
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <returns>如果异常可重试则返回true，否则返回false</returns>
    bool IsRetryableException(Exception ex);
    
    /// <summary>
    /// 检查异常是否为数据错误异常
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <returns>如果异常为数据错误异常则返回true，否则返回false</returns>
    bool IsDataErrorException(Exception ex);
    
    /// <summary>
    /// 计算指数退避时间
    /// </summary>
    /// <param name="retryCount">重试次数</param>
    /// <returns>计算得到的退避时间</returns>
    TimeSpan CalculateExponentialBackoff(int retryCount);
}
