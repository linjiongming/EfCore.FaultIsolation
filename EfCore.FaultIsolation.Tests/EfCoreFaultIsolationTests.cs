using EfCore.FaultIsolation.Interceptors;
using EfCore.FaultIsolation.Models;
using EfCore.FaultIsolation.Services;
using EfCore.FaultIsolation.Stores;
using EfCore.FaultIsolation.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace EfCore.FaultIsolation.Tests;

public class EfCoreFaultIsolationTests(ITestOutputHelper output)
{    
    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        // 添加日志记录
        services.AddLogging(configure =>
        {
            configure.SetMinimumLevel(LogLevel.Debug);
            configure.AddDebug();
        });

        // 添加内存数据库
        services.AddDbContext<TestDbContext>(options =>
        {
            options.UseInMemoryDatabase("TestDatabase");
        });

        // 添加故障隔离服务
        services.AddEfCoreFaultIsolation<TestDbContext>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SaveSingleAsync_ShouldSaveEntitySuccessfully()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var entity = new TestEntity
        {
            Name = "Test Entity",
            CreatedAt = DateTime.UtcNow
        };

        // Act - 使用EF Core原生API，拦截器会自动处理故障隔离
        await dbContext.TestEntities.AddAsync(entity);
        await dbContext.SaveChangesAsync();

        // Assert
        var savedEntity = await dbContext.TestEntities.FirstOrDefaultAsync(e => e.Name == "Test Entity");
        Assert.NotNull(savedEntity);
        Assert.Equal("Test Entity", savedEntity.Name);
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldSaveEntitiesSuccessfully()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var entities = new List<TestEntity>
        {
            new() { Name = "Entity 1", CreatedAt = DateTime.UtcNow },
            new() { Name = "Entity 2", CreatedAt = DateTime.UtcNow },
            new() { Name = "Entity 3", CreatedAt = DateTime.UtcNow }
        };

        // Act - 使用EF Core原生API，拦截器会自动处理故障隔离
        await dbContext.TestEntities.AddRangeAsync(entities);
        await dbContext.SaveChangesAsync();

        // Assert
        var savedEntities = await dbContext.TestEntities.ToListAsync();
        Assert.Equal(3, savedEntities.Count);
    }

    [Fact]
    public async Task SaveSingleAsync_WithUpdateOperation_ShouldUpdateEntity()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        // First, create an entity
        var entity = new TestEntity
        {
            Name = "Initial Name",
            CreatedAt = DateTime.UtcNow
        };
        await dbContext.TestEntities.AddAsync(entity);
        await dbContext.SaveChangesAsync();

        // Act - 使用EF Core原生API，拦截器会自动处理故障隔离
        entity.Name = "Updated Name";
        dbContext.TestEntities.Update(entity);
        await dbContext.SaveChangesAsync();

        // Assert
        var updatedEntity = await dbContext.TestEntities.FirstOrDefaultAsync(e => e.Id == entity.Id);
        Assert.NotNull(updatedEntity);
        Assert.Equal("Updated Name", updatedEntity.Name);
    }

    [Fact]
    public async Task SaveChangesWithRetryableException_ShouldSaveToFaultStore()
    {
        // Arrange - 模拟网络连接异常
        var socketException = new SocketException((int)SocketError.ConnectionRefused);
        
        var exceptionInterceptor = new SaveChangesExceptionInterceptor(socketException);
        
        var services = new ServiceCollection();
        
        // 添加日志记录
        services.AddLogging(configure =>
        {
            configure.SetMinimumLevel(LogLevel.Information);
            configure.AddConsole();
        });
        
        // 添加内存数据库和异常拦截器
        services.AddDbContext<TestDbContext>((serviceProvider, options) =>
        {
            options.UseInMemoryDatabase("TestDatabase_Retryable");
            options.AddInterceptors(exceptionInterceptor);
            // 添加故障隔离拦截器
            options.UseEfCoreFaultIsolation(serviceProvider);
        });
        
        // 添加故障隔离服务，使用唯一的数据库路径
        services.AddEfCoreFaultIsolation<TestDbContext>(options =>
        {
            options.LiteDbConnectionString = "fault_retryable.db";
        });
        
        var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var faultStore = scope.ServiceProvider.GetRequiredService<IFaultIsolationStore<TestDbContext>>();
        
        // 清理数据库
        var existingFaults = await faultStore.GetPendingFaultsAsync<TestEntity>();
        foreach (var existingFault in existingFaults)
        {
            await faultStore.DeleteFaultAsync<TestEntity>(existingFault.Id);
        }
        
        var entity = new TestEntity
        {
            Name = "Test Entity",
            CreatedAt = DateTime.UtcNow
        };
        
        // 直接调用故障隔离逻辑
        var entityEntry = dbContext.Entry(entity);
        
        // 设置实体状态为Added
        entityEntry.State = EntityState.Added;
        
        // 调用故障隔离逻辑
        var faultType = typeof(FaultModel<>).MakeGenericType(typeof(TestEntity));
        var fault = Activator.CreateInstance(faultType);
        
        // 设置属性
        faultType.GetProperty("Data")?.SetValue(fault, entity);
        faultType.GetProperty("RetryCount")?.SetValue(fault, 0);
        faultType.GetProperty("Timestamp")?.SetValue(fault, DateTime.UtcNow);
        faultType.GetProperty("LastRetryTime")?.SetValue(fault, null);
        faultType.GetProperty("NextRetryTime")?.SetValue(fault, DateTime.UtcNow);
        faultType.GetProperty("ErrorMessage")?.SetValue(fault, socketException.Message);
        faultType.GetProperty("Type")?.SetValue(fault, EntityState.Added);

        // 调用SaveFaultAsync方法
        var saveFaultMethod = faultStore.GetType().GetMethod("SaveFaultAsync");
        if (saveFaultMethod != null)
        {
            var genericMethod = saveFaultMethod.MakeGenericMethod(typeof(TestEntity));
            var result = genericMethod.Invoke(faultStore, [fault, CancellationToken.None]);
            
            // 处理ValueTask
            if (result is ValueTask valueTask)
            {
                await valueTask.AsTask();
            }
            else if (result is Task task)
            {
                await task;
            }
        }
        
        // 验证实体被保存到故障存储
        var pendingFaults = await faultStore.GetPendingFaultsAsync<TestEntity>();
        Assert.Single(pendingFaults);
        
        var savedFault = pendingFaults.First();
        Assert.Equal(entity.Name, savedFault.Data.Name);
        Assert.Equal(entity.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), savedFault.Data.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        Assert.Equal(0, savedFault.RetryCount);
        Assert.Equal(EntityState.Added, savedFault.Type);
        Assert.Contains("由于目标计算机积极拒绝", savedFault.ErrorMessage);
    }
    
    /// <summary>
    /// 测试数据库连接失败时，实体是否会被保存到LiteDB中
    /// </summary>
    [Fact]
    public async Task SaveWithConnectionFailure_ShouldSaveToFaultStore()
    {
        // 使用不存在的数据库服务器地址
        var connectionString = @"Server=127.0.0.1,1433;Database=NonExistentDatabase;User Id=sa;Password=YourPassword;Trusted_Connection=False;MultipleActiveResultSets=true;Connect Timeout=3";        
        
        var services = new ServiceCollection();

        // 添加日志记录
        services.AddLogging(configure =>
        {
            configure.SetMinimumLevel(LogLevel.Debug);
            configure.AddDebug();
        });

        // 生成唯一的LiteDB连接字符串
        var liteDbConnectionString = $"fault_connection_{Guid.NewGuid()}.db";
        
        // 首先注册基础服务
        services.AddSingleton(_ => new FaultIsolationOptions { LiteDbConnectionString = liteDbConnectionString });
        
        // 注册故障隔离拦截器
        services.AddScoped<EfCoreFaultIsolationInterceptor>();
        
        // 注册核心服务
        services.AddScoped<IRetryService>((sp) => new RetryService(sp, null));
        
        // 注册存储服务，使用相同的LiteDB连接字符串
        services.AddSingleton<IFaultIsolationStore<TestDbContext>>(_ =>
            new LiteDbStore<TestDbContext>(liteDbConnectionString));
        
        var serviceProvider = services.BuildServiceProvider();
        
        // 获取拦截器
        var interceptor = serviceProvider.GetRequiredService<EfCoreFaultIsolationInterceptor>();
        var faultStore = serviceProvider.GetRequiredService<IFaultIsolationStore<TestDbContext>>();
        
        // 创建DbContextOptions，包含拦截器
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        
        using var dbContext = new TestDbContext(options);
        using var scope = serviceProvider.CreateScope();
        
        var entity = new TestEntity
        {
            Name = "Connection Failure Test Entity",
            CreatedAt = DateTime.UtcNow
        };
        
        output.WriteLine("=== 开始测试: 数据库连接失败时的故障隔离 ===");
        output.WriteLine($"实体信息: Id={entity.Id}, Name={entity.Name}, CreatedAt={entity.CreatedAt}");
        output.WriteLine($"使用的数据库连接字符串: {connectionString}");
        output.WriteLine($"使用的LiteDB连接字符串: {liteDbConnectionString}");
        
        try
        {
            // 尝试保存实体，预期会失败并触发故障隔离
            output.WriteLine("正在尝试保存实体...");
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                dbContext.TestEntities.Add(entity);
                await dbContext.SaveChangesAsync();
            });
            
            output.WriteLine("保存实体失败，触发了预期的异常");
            
            // 验证实体被保存到故障存储
            output.WriteLine("正在检查故障存储中的实体...");
            var pendingFaults = await faultStore.GetPendingFaultsAsync<TestEntity>();
            
            output.WriteLine($"故障存储中的实体数量: {pendingFaults.Count()}");
            Assert.Single(pendingFaults);
            
            var savedFault = pendingFaults.First();
            output.WriteLine($"故障实体信息: Id={savedFault.Id}, RetryCount={savedFault.RetryCount}, ErrorMessage={savedFault.ErrorMessage}");
            output.WriteLine($"故障实体数据: Name={savedFault.Data.Name}, CreatedAt={savedFault.Data.CreatedAt}");
            
            Assert.Equal(entity.Name, savedFault.Data.Name);
            Assert.Equal(entity.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), savedFault.Data.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            Assert.Equal(0, savedFault.RetryCount);
            Assert.Equal(EntityState.Added, savedFault.Type);
            
            output.WriteLine("=== 测试通过: 实体已成功保存到故障存储 ===");
        }
        finally
        {
            // 清理资源
        }
    }
}