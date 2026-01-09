using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.EntityFrameworkCore;
using EfCore.FaultIsolation.Models;

namespace EfCore.FaultIsolation.Stores;

public class LiteDbStore<TDbContext> : IFaultIsolationStore<TDbContext> where TDbContext : DbContext
{
    private readonly LiteDatabase _database;
    
    private static string GetDbName()
    {
        var dbContextTypeName = typeof(TDbContext).Name;
        
        // Generate dbname by removing "Context" or "DbContext" suffix
        if (dbContextTypeName.EndsWith("DbContext", System.StringComparison.OrdinalIgnoreCase))
        {
            return dbContextTypeName[..^"DbContext".Length];
        }
        else if (dbContextTypeName.EndsWith("Context", System.StringComparison.OrdinalIgnoreCase))
        {
            return dbContextTypeName[..^"Context".Length];
        }
        else
        {
            return dbContextTypeName;
        }
    }
    
    public LiteDbStore(string? connectionString = null)
    {
        // Only generate default connection string if null
        if (connectionString == null)
        {
            var dbName = GetDbName();
            connectionString = $"{dbName}-fault.db";
        }
        
        _database = new LiteDatabase(connectionString);
    }
    
    private ILiteCollection<FaultModel<TEntity>> GetFaultCollection<TEntity>() where TEntity : class
    {
        var collectionName = $"{typeof(TEntity).Name}_Fault";
        return _database.GetCollection<FaultModel<TEntity>>(collectionName);
    }
    
    private ILiteCollection<DeadLetter<TEntity>> GetDeadLetterCollection<TEntity>() where TEntity : class
    {
        var collectionName = $"{typeof(TEntity).Name}_Dead";
        return _database.GetCollection<DeadLetter<TEntity>>(collectionName);
    }
    
    public ValueTask SaveFaultAsync<TEntity>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) where TEntity : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = GetFaultCollection<TEntity>();
        collection.Insert(fault);
        return ValueTask.CompletedTask;
    }
    
    public ValueTask<IEnumerable<FaultModel<TEntity>>> GetPendingFaultsAsync<TEntity>(int batchSize = 100, CancellationToken cancellationToken = default) where TEntity : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = GetFaultCollection<TEntity>();
        var faults = collection
            .Find(x => x.NextRetryTime <= System.DateTime.UtcNow)
            .OrderBy(x => x.Timestamp)
            .Take(batchSize)
            .ToList();
        
        return ValueTask.FromResult((IEnumerable<FaultModel<TEntity>>)faults);
    }
    
    public ValueTask DeleteFaultAsync<TEntity>(Guid id, CancellationToken cancellationToken = default) where TEntity : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = GetFaultCollection<TEntity>();
        collection.Delete(id);
        return ValueTask.CompletedTask;
    }
    
    public ValueTask UpdateFaultAsync<TEntity>(FaultModel<TEntity> fault, CancellationToken cancellationToken = default) where TEntity : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = GetFaultCollection<TEntity>();
        collection.Update(fault);
        return ValueTask.CompletedTask;
    }
    
    public ValueTask SaveDeadLetterAsync<TEntity>(DeadLetter<TEntity> deadLetter, CancellationToken cancellationToken = default) where TEntity : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = GetDeadLetterCollection<TEntity>();
        collection.Insert(deadLetter);
        return ValueTask.CompletedTask;
    }
    
    public ValueTask<int> GetPendingFaultCountAsync<TEntity>(CancellationToken cancellationToken = default) where TEntity : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = GetFaultCollection<TEntity>();
        var count = collection.Count(x => x.NextRetryTime <= System.DateTime.UtcNow);
        return ValueTask.FromResult(count);
    }
    
    public ValueTask<IEnumerable<string>> GetAllFaultCollectionNamesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collectionNames = _database.GetCollectionNames()
            .Where(name => name.EndsWith("_Fault"))
            .ToList();
        
        return ValueTask.FromResult((IEnumerable<string>)collectionNames);
    }
    
    public ValueTask<IEnumerable<object>> GetPendingFaultsByCollectionNameAsync(string collectionName, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collection = _database.GetCollection(collectionName);
        var now = System.DateTime.UtcNow;
        
        var faults = collection
            .Find(BsonExpression.Create($"{{NextRetryTime: {{$lte: ISODate('{now:yyyy-MM-ddTHH:mm:ss.fffZ}')}}}}", true))
            .OrderBy(x => x["Timestamp"])
            .Take(batchSize)
            .ToList()
            .Cast<object>();
        
        return ValueTask.FromResult((IEnumerable<object>)faults);
    }
}
