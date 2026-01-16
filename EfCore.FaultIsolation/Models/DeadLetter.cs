using LiteDB;

namespace EfCore.FaultIsolation.Models;

/// <summary>
/// 死信队列模型类，用于存储无法重试成功的实体数据和相关失败信息
/// </summary>
/// <typeparam name="TEntity">实体类型</typeparam>
public class DeadLetter<TEntity>
{
    /// <summary>
    /// 死信队列项唯一标识符
    /// </summary>
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// 失败的实体数据
    /// </summary>
    public TEntity Data { get; set; } = default!;
    
    /// <summary>
    /// 总共重试次数
    /// </summary>
    public int TotalRetryCount { get; set; }
    
    /// <summary>
    /// 放入死信队列的时间戳（UTC）
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 最后一次重试时间（UTC）
    /// </summary>
    public DateTime? LastRetryTime { get; set; }
    
    /// <summary>
    /// 错误消息
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// 失败原因
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;
    
    /// <summary>
    /// 操作类型（增删改）
    /// </summary>
    public EntityState Type { get; set; } = EntityState.Added;
}
