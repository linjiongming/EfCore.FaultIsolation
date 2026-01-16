using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Net.Sockets;

namespace EfCore.FaultIsolation.Services;

/// <summary>
/// 重试服务实现类，用于处理实体操作的重试逻辑和异常分类
/// </summary>
public class RetryService(IServiceProvider serviceProvider, TimeSpan? initialRetryDelay = null) : IRetryService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;    private readonly TimeSpan _initialRetryDelay = initialRetryDelay ?? TimeSpan.FromSeconds(10);


    /// <summary>
    /// 最大重试次数
    /// </summary>
    public int MaxRetries { get; set; } = 5;

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
    public bool IsRetryableException(Exception ex)
    {
        if (ex is null)
            return false;

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
                // 获取实际错误码（处理SQL Server的SqlException）
                int actualErrorCode = dbEx.ErrorCode;
                
                // 尝试使用反射获取SQL Server的SqlException.Number属性
                try
                {
                    var sqlExceptionType = dbEx.GetType();
                    if (sqlExceptionType.Name == "SqlException")
                    {
                        var numberProperty = sqlExceptionType.GetProperty("Number");
                        if (numberProperty != null)
                        {
                            actualErrorCode = (int)numberProperty.GetValue(dbEx)!;
                        }
                    }
                }
                catch (Exception)
                {
                    // 如果反射失败，使用默认的ErrorCode
                }
                
                return actualErrorCode == -2146232060 || // Timeout
                       actualErrorCode == 53 || // SQL Server: Server not found
                       actualErrorCode == 121 || // SQL Server: Network error
                       actualErrorCode == 10060 || // TCP: Connection timed out
                       actualErrorCode == 10061 || // TCP: Connection refused
                       actualErrorCode == 10054 || // TCP: Connection reset by peer
                       actualErrorCode == -2 || // SQL Server: Timeout expired
                       actualErrorCode == 258 || // SQL Server: Timeout expired
                       actualErrorCode == 11001; // Winsock: Host not found
            }

            // EF Core 连接相关异常
            if (exception is InvalidOperationException invalidOpEx)
            {
                // 检查是否是连接相关的InvalidOperationException
                // 这里保留了一些必要的消息检查，因为某些连接问题可能只通过消息标识
                return invalidOpEx.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("database (currently unavailable)", StringComparison.OrdinalIgnoreCase);
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

        return isRetryable;
    }

    /// <summary>
    /// 检查异常是否是数据错误异常
    /// </summary>
    /// <param name="ex">要检查的异常</param>
    /// <returns>如果异常是数据错误异常，则返回true；否则返回false</returns>
    public bool IsDataErrorException(Exception ex)
    {
        if (ex is null)
            return false;

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
                return invalidOpEx.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("length", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("truncate", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                       invalidOpEx.Message.Contains("null", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        });

        return isDataError;
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


}
