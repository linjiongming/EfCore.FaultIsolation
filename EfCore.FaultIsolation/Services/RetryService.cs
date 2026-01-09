using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.FaultIsolation.Services;

/// <summary>
/// 重试服务实现类，用于处理实体操作的重试逻辑和异常分类
/// </summary>
public class RetryService : IRetryService
{
    private int _maxRetries = 5;
    private readonly TimeSpan _initialRetryDelay;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentBag<Type> _fatalExceptionTypes = new ConcurrentBag<Type>();
    
    /// <summary>
    /// 初始化 RetryService 实例，使用默认的初始重试延迟时间（1秒）
    /// </summary>
    /// <param name="serviceProvider">服务提供程序实例</param>
    public RetryService(IServiceProvider serviceProvider)
    {
        _initialRetryDelay = TimeSpan.FromSeconds(1);
        _serviceProvider = serviceProvider;
        
        // 添加默认的致命异常类型（数据异常）
        _fatalExceptionTypes.Add(typeof(ValidationException));
        _fatalExceptionTypes.Add(typeof(DbUpdateConcurrencyException));
        _fatalExceptionTypes.Add(typeof(DbUpdateException));
    }
    
    /// <summary>
    /// 初始化 RetryService 实例，使用自定义的初始重试延迟时间
    /// </summary>
    /// <param name="initialRetryDelay">初始重试延迟时间</param>
    /// <param name="serviceProvider">服务提供程序实例</param>
    public RetryService(TimeSpan initialRetryDelay, IServiceProvider serviceProvider)
    {
        _initialRetryDelay = initialRetryDelay;
        _serviceProvider = serviceProvider;
        
        // 添加默认的致命异常类型（数据异常）
        _fatalExceptionTypes.Add(typeof(ValidationException));
        _fatalExceptionTypes.Add(typeof(DbUpdateConcurrencyException));
        _fatalExceptionTypes.Add(typeof(DbUpdateException));
    }
    
    /// <summary>
    /// 递归检查异常链中是否有任何异常匹配给定的谓词
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <param name="predicate">用于检查异常的谓词</param>
    /// <returns>如果找到匹配的异常则返回true，否则返回false</returns>
    private static bool HasMatchingException(Exception? ex, Func<Exception, bool> predicate)
    {
        if (ex == null)
            return false;
        
        // 检查当前异常
        if (predicate(ex))
            return true;
        
        // 递归检查内部异常
        return ex.InnerException != null && HasMatchingException(ex.InnerException, predicate);
    }
    
    /// <summary>
    /// 批量重试实体操作
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="entities">要重试的实体集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RetryBatchAsync<TEntity, TDbContext>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        
        await dbContext.AddRangeAsync(entities, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
    
    /// <summary>
    /// 单个实体操作重试
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <typeparam name="TDbContext">数据库上下文类型</typeparam>
    /// <param name="entity">要重试的实体</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RetrySingleAsync<TEntity, TDbContext>(TEntity entity, CancellationToken cancellationToken = default) 
        where TEntity : class 
        where TDbContext : DbContext
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        
        await dbContext.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
    
    /// <summary>
    /// 检查异常是否是可重试的异常
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <returns>如果异常是可重试的，则返回true；否则返回false</returns>
    public ValueTask<bool> IsRetryableException(Exception ex)
    {
        if (ex is null)
            return ValueTask.FromResult(false);
            
        // 检查异常链中是否有可重试的异常类型
        bool isRetryable = HasMatchingException(ex, exception =>
        {
            // 网络和连接相关异常类型
            if (exception is SocketException)
                return true;
            
            // 超时异常
            if (exception is TimeoutException)
                return true;
            
            // 数据库连接异常
            if (exception is DbException dbEx)
            {
                return dbEx.ErrorCode == -2146232060 || // Timeout
                       dbEx.ErrorCode == 53 || // SQL Server: Server not found
                       dbEx.ErrorCode == 121 || // SQL Server: Network error
                       dbEx.ErrorCode == 10060 || // TCP: Connection timed out
                       dbEx.ErrorCode == 10061 || // TCP: Connection refused
                       dbEx.ErrorCode == 10054; // TCP: Connection reset by peer
            }
            
            // EF Core 连接相关异常
            if (exception is InvalidOperationException invalidOpEx)
            {
                // 检查是否是连接相关的InvalidOperationException
                // 这里保留了一些必要的消息检查，因为某些连接问题可能只通过消息标识
                return invalidOpEx.Message.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("database (currently unavailable)", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            
            // 网络操作取消异常
            if (exception is OperationCanceledException opCancelEx)
            {
                // 如果是由于超时或网络异常导致的取消，则可以重试
                return opCancelEx.InnerException is TimeoutException ||
                       opCancelEx.InnerException is SocketException;
            }
            
            return false;
        });
        
        return ValueTask.FromResult(isRetryable);
    }
    
    /// <summary>
    /// 检查异常是否是数据错误异常
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <returns>如果异常是数据错误异常，则返回true；否则返回false</returns>
    public ValueTask<bool> IsDataErrorException(Exception ex)
    {
        if (ex is null)
            return ValueTask.FromResult(false);
            
        // 检查异常链中是否有数据错误相关的异常类型
        bool isDataError = HasMatchingException(ex, exception =>
        {
            // 数据更新异常
            if (exception is DbUpdateException dbUpdateEx)
            {
                // 如果有Entries，可能是数据验证错误
                if (dbUpdateEx.Entries.Any())
                {
                    return true;
                }
            }
            
            // EF Core 并发异常
            if (exception is DbUpdateConcurrencyException)
            {
                return true;
            }
            
            // 数据验证异常
            if (exception is ValidationException)
            {
                return true;
            }
            
            // 数据库异常 - 数据相关错误
            if (exception is DbException dbEx)
            {
                return dbEx.ErrorCode == 2601 || // SQL Server: Duplicate key
                       dbEx.ErrorCode == 2627 || // SQL Server: Unique constraint violation
                       dbEx.ErrorCode == 8152 || // SQL Server: String or binary data would be truncated
                       dbEx.ErrorCode == 229 || // SQL Server: Permission denied
                       dbEx.ErrorCode == 547 || // SQL Server: Foreign key constraint violation
                       dbEx.ErrorCode == 515; // SQL Server: Cannot insert the value NULL
            }
            
            // 无效操作异常 - 数据相关错误
            if (exception is InvalidOperationException invalidOpEx)
            {
                // 检查是否是数据相关的InvalidOperationException
                // 这里保留了一些必要的消息检查，因为某些数据问题可能只通过消息标识
                return invalidOpEx.Message.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("constraint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("length", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("overflow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("truncate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       invalidOpEx.Message.IndexOf("null", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            
            return false;
        });
        
        return ValueTask.FromResult(isDataError);
    }
    
    /// <summary>
    /// 计算指数退避延迟时间
    /// </summary>
    /// <param name="retryCount">重试次数</param>
    /// <returns>计算后的退避延迟时间</returns>
    public TimeSpan CalculateExponentialBackoff(int retryCount)
    {
        var maxDelay = TimeSpan.FromMinutes(30);
        
        // 指数退避：baseDelay * (2^retryCount) + 随机抖动
        var delay = _initialRetryDelay.TotalSeconds * Math.Pow(2, retryCount);
        var jitter = new Random().Next(0, 1000);
        
        return TimeSpan.FromTicks(Math.Min((TimeSpan.FromSeconds(delay) + TimeSpan.FromMilliseconds(jitter)).Ticks, maxDelay.Ticks));
    }
    
    /// <summary>
    /// 检查异常是否是致命异常
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <returns>如果异常是致命异常，则返回true；否则返回false</returns>
    public ValueTask<bool> IsFatalException(Exception ex)
    {
        if (ex is null)
            return ValueTask.FromResult(false);
            
        // 检查异常链中是否有致命异常类型
        bool isFatal = HasMatchingException(ex, exception =>
        {
            // 检查是否是配置的致命异常类型
            foreach (var fatalType in _fatalExceptionTypes)
            {
                if (fatalType.IsAssignableFrom(exception.GetType()))
                    return true;
            }
            
            return false;
        });
        
        return ValueTask.FromResult(isFatal);
    }
    
    /// <summary>
    /// 配置致命异常类型集合
    /// </summary>
    /// <param name="fatalExceptionTypes">致命异常类型集合</param>
    /// <exception cref="ArgumentNullException">当fatalExceptionTypes为null时抛出</exception>
    /// <exception cref="ArgumentException">当任何类型不是Exception的子类时抛出</exception>
    public void ConfigureFatalExceptions(IEnumerable<Type> fatalExceptionTypes)
    {
        if (fatalExceptionTypes == null)
            throw new ArgumentNullException(nameof(fatalExceptionTypes));
            
        // 清空现有配置
        _fatalExceptionTypes.Clear();
        
        // 添加新的致命异常类型
        foreach (var type in fatalExceptionTypes)
        {
            if (typeof(Exception).IsAssignableFrom(type))
            {
                _fatalExceptionTypes.Add(type);
            }
            else
            {
                throw new ArgumentException($"Type {type.FullName} is not a subclass of Exception.");
            }
        }
    }
    
    /// <summary>
    /// 添加单个致命异常类型
    /// </summary>
    /// <param name="exceptionType">要添加的异常类型</param>
    /// <exception cref="ArgumentNullException">当exceptionType为null时抛出</exception>
    /// <exception cref="ArgumentException">当类型不是Exception的子类时抛出</exception>
    public void AddFatalExceptionType(Type exceptionType)
    {
        if (exceptionType == null)
            throw new ArgumentNullException(nameof(exceptionType));
            
        if (typeof(Exception).IsAssignableFrom(exceptionType))
        {
            _fatalExceptionTypes.Add(exceptionType);
        }
        else
        {
            throw new ArgumentException($"Type {exceptionType.FullName} is not a subclass of Exception.");
        }
    }
    
    /// <summary>
    /// 移除单个致命异常类型
    /// </summary>
    /// <param name="exceptionType">要移除的异常类型</param>
    /// <exception cref="ArgumentNullException">当exceptionType为null时抛出</exception>
    public void RemoveFatalExceptionType(Type exceptionType)
    {
        if (exceptionType == null)
            throw new ArgumentNullException(nameof(exceptionType));
            
        // 从ConcurrentBag中移除指定类型
        // 注意：ConcurrentBag不支持直接移除，需要重新创建
        var newBag = new ConcurrentBag<Type>();
        foreach (var type in _fatalExceptionTypes)
        {
            if (type != exceptionType)
            {
                newBag.Add(type);
            }
        }
        
        // 替换旧的ConcurrentBag
        _fatalExceptionTypes.Clear();
        foreach (var type in newBag)
        {
            _fatalExceptionTypes.Add(type);
        }
    }
    
    /// <summary>
    /// 获取最大重试次数
    /// </summary>
    /// <returns>当前配置的最大重试次数</returns>
    public int GetMaxRetries()
    {
        return _maxRetries;
    }
    
    /// <summary>
    /// 配置最大重试次数
    /// </summary>
    /// <param name="maxRetries">要设置的最大重试次数</param>
    /// <exception cref="ArgumentOutOfRangeException">当maxRetries为负数时抛出</exception>
    public void ConfigureMaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries must be non-negative.");
        }
        
        _maxRetries = maxRetries;
    }
}
