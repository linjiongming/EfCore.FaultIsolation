using System;
using LiteDB;

namespace EfCore.FaultIsolation.Models;

public class FaultModel<TEntity>
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public TEntity Data { get; set; } = default!;
    
    public int RetryCount { get; set; } = 0;
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public DateTime? LastRetryTime { get; set; }
    
    public DateTime NextRetryTime { get; set; } = DateTime.UtcNow;
    
    public string? ErrorMessage { get; set; }
}
