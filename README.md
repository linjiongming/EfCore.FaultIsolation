# EfCore.FaultIsolation

EfCore.FaultIsolation是一个用于Entity Framework Core的容错隔离库，提供了自动重试、故障隔离和死信队列功能。

## 主要功能

- 批量和单条数据保存的容错处理
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
builder.Services.AddEfCoreFaultIsolation(options =>
{
    options.UseLiteDbStore();
    options.UseHangfireScheduler();
});
```

### 3. 启动服务

```csharp
var faultIsolationService = app.Services.GetRequiredService<EfCoreFaultIsolationService>();
await faultIsolationService.StartAsync();
```

### 4. 使用服务

```csharp
// 批量保存
var entities = new List<Product> { new Product { Name = "Product 1" }, new Product { Name = "Product 2" } };
await faultIsolationService.SaveBatchAsync<Product, AppDbContext>(entities);

// 单条保存
var product = new Product { Name = "Product 3" };
await faultIsolationService.SaveSingleAsync<Product, AppDbContext>(product);

// 手动触发批量重试（可选，仅在需要时使用）
faultIsolationService.ConfigureRecurringRetry<Product, AppDbContext>();
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
- **数据错误(DataError)**: 单条数据错误时降级为逐条插入

## 健康检查

库内置了数据库连接健康检查功能，可以监控数据库连接状态，并在连接恢复时自动触发重试任务。

## 配置选项

- `UseLiteDbStore()`: 使用LiteDB作为故障存储
- `UseHangfireScheduler()`: 使用Hangfire作为定时任务调度器

## 贡献

欢迎提交Issue和Pull Request！

## 许可证

MIT License