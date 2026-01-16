using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.FaultIsolation.Tests.TestUtils;

/// <summary>
/// 用于测试的SaveChanges异常拦截器，用于模拟保存失败
/// </summary>
/// <remarks>
/// 初始化异常拦截器
/// </remarks>
/// <param name="exceptionToThrow">要抛出的异常</param>
/// <param name="throwOnCallNumber">在第几次调用SaveChanges时抛出异常（从1开始）</param>
public class SaveChangesExceptionInterceptor(Exception exceptionToThrow, int throwOnCallNumber = 1) : SaveChangesInterceptor
{
    private readonly Exception _exceptionToThrow = exceptionToThrow;
    private readonly int _throwOnCallNumber = throwOnCallNumber;
    private int _callCount = 0;
    
    /// <summary>
    /// 是否已经触发了异常
    /// </summary>
    public bool ExceptionTriggered { get; private set; } = false;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        _callCount++;
        if (_callCount == _throwOnCallNumber)
        {
            ExceptionTriggered = true;
            throw _exceptionToThrow;
        }
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        _callCount++;
        if (_callCount == _throwOnCallNumber)
        {
            ExceptionTriggered = true;
            throw _exceptionToThrow;
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}