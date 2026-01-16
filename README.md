# EfCore.FaultIsolation

EfCore.FaultIsolation是一个用于Entity Framework Core的容错隔离库，提供了自动重试、故障隔离和死信队列功能。

## 主要功能

- **自动容错处理**：通过EF Core拦截器自动处理所有SaveChanges操作
- 基于异常类型的智能重试策略
- 故障数据的隔离存储（使用LiteDB）
- 死信队列机制
- 数据库连接健康检查
- Hangfire集成的定时重试任务

## 使用方法

### 1. 安装依赖

```bash
dotnet add package EfCore.FaultIsolation
```

### 2. 配置服务

在Startup.cs或Program.cs中配置服务：

```csharp
builder.Services.AddFaultIsolation<AppDbContext>();
```

### 3. 配置DbContext

在DbContext的OnConfiguring方法中添加拦截器，或在服务配置时添加：

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer("connection-string");
    options.AddInterceptors(new FaultIsolationInterceptor());
});
```

或者通过服务提供程序获取拦截器：

```csharp
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseSqlServer("connection-string");
    var interceptor = serviceProvider.GetRequiredService<FaultIsolationInterceptor>();
    options.AddInterceptors(interceptor);
});
```

### 4. 使用服务

**现在可以直接使用EF Core的原生API**，故障隔离会自动生效：

```csharp
// 批量保存
var entities = new List<Product> { new Product { Name = "Product 1" }, new Product { Name = "Product 2" } };
dbContext.AddRange(entities);
await dbContext.SaveChangesAsync();

// 单条保存
var product = new Product { Name = "Product 3" };
dbContext.Add(product);
await dbContext.SaveChangesAsync();

// 更新操作
product.Name = "Updated Product";
dbContext.Update(product);
await dbContext.SaveChangesAsync();

// 删除操作
dbContext.Remove(product);
await dbContext.SaveChangesAsync();
```

**配置定期重试任务**（可选）：

```csharp
// 在需要的地方获取服务
var faultIsolationService = serviceProvider.GetRequiredService<FaultIsolationService<AppDbContext>>();
faultIsolationService.ConfigureRecurringRetry<Product>();
```

## 核心概念

### FaultModel

表示需要重试的故障数据，包含：
- Data: 原始数据
- RetryCount: 已重试次数
- Timestamp: 故障发生时间
- LastRetryTime: 最后重试时间
- NextRetryTime: 下次重试时间
- ErrorMessage: 错误信息

### DeadLetter

表示无法重试的数据，包含：
- Data: 原始数据
- TotalRetryCount: 总重试次数
- Timestamp: 故障发生时间
- LastRetryTime: 最后重试时间
- ErrorMessage: 错误信息
- FailureReason: 失败原因

### 异常处理策略

- **致命异常(Fatal)**: 直接保存到死信队列
- **可重试异常(Retryable)**: 保存到故障存储，稍后重试
- **数据错误(DataError)**: 单条数据错误时保存到死信队列

## 健康检查

库内置了数据库连接健康检查功能，可以监控数据库连接状态，并在连接恢复时自动触发重试任务。

## 配置选项

可以通过FaultIsolationOptions配置库的行为：

```csharp
builder.Services.AddFaultIsolation<AppDbContext>(options =>
{
    options.InitialRetryDelay = TimeSpan.FromSeconds(5);
    options.MaxRetries = 3;
    options.HealthCheckIntervalSeconds = 30;
    // 自定义LiteDB连接字符串
    options.LiteDbConnectionString = "Filename=custom_fault.db;Connection=shared";
    // 自定义Hangfire连接字符串
    options.HangfireConnectionString = "Filename=custom_hangfire.db;Connection=shared";
});
```

## 核心组件

### FaultIsolationInterceptor

EF Core拦截器，自动拦截所有SaveChanges操作，实现故障隔离和重试逻辑。

### FaultIsolationService

后台服务（IHostedService），负责：
- 恢复挂起的重试任务
- 监控数据库连接状态
- 配置和管理定期重试任务

## 贡献

欢迎提交Issue和Pull Request！

## 许可证

MIT License