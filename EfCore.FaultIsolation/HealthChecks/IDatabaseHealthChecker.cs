using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EfCore.FaultIsolation.HealthChecks;

public interface IDatabaseHealthChecker<TDbContext> where TDbContext : DbContext
{
    event EventHandler DatabaseConnected;
    
    Task<bool> IsHealthyAsync();
    
    void StartMonitoring(int intervalSeconds = 30);
    
    void StopMonitoring();
}
