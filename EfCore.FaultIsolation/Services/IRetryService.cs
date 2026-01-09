using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EfCore.FaultIsolation.Services;

public interface IRetryService
{
    Task RetryBatchAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext;
    
    Task RetrySingleAsync<TEntity, TDbContext>(TEntity entity, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext;
    
    ValueTask<bool> IsRetryableException(Exception ex);
    
    ValueTask<bool> IsDataErrorException(Exception ex);
    
    ValueTask<bool> IsFatalException(Exception ex);
    
    TimeSpan CalculateExponentialBackoff(int retryCount);
    
    void ConfigureFatalExceptions(IEnumerable<Type> fatalExceptionTypes);
    
    void AddFatalExceptionType(Type exceptionType);
    
    void RemoveFatalExceptionType(Type exceptionType);
    
    int GetMaxRetries();
    
    void ConfigureMaxRetries(int maxRetries);
}
