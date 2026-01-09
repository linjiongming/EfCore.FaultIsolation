using System;
using LiteDB;

namespace EfCore.FaultIsolation.Models;

public class DeadLetter<TEntity>
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public TEntity Data { get; set; } = default!;
    
    public int TotalRetryCount { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public DateTime? LastRetryTime { get; set; }
    
    public string ErrorMessage { get; set; } = string.Empty;
    
    public string FailureReason { get; set; } = string.Empty;
}
