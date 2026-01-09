using System;
using LiteDB;

namespace EfCore.FaultIsolation.Models;

/// <summary>
/// 故障模型类，用于存储需要重试的实体数据和相关重试信息
/// </summary>
/// <typeparam name="TEntity">实体类型</typeparam>
public class FaultModel<TEntity>
{
    /// <summary>
    /// 故障模型唯一标识符
    /// </summary>
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// 需要重试的实体数据
    /// </summary>
    public TEntity Data { get; set; } = default!;
    
    /// <summary>
    /// 已重试次数
    /// </summary>
    public int RetryCount { get; set; } = 0;
    
    /// <summary>
    /// 故障创建时间戳（UTC）
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 上次重试时间（UTC）
    /// </summary>
    public DateTime? LastRetryTime { get; set; }
    
    /// <summary>
    /// 下次重试时间（UTC）
    /// </summary>
    public DateTime NextRetryTime { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }
}
