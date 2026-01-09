using System;using System.Threading.Tasks;

namespace EfCore.FaultIsolation.HealthChecks;

public interface IDatabaseHealthChecker
{
    event EventHandler DatabaseConnected;
    
    Task<bool> IsHealthyAsync();
    
    void StartMonitoring(int intervalSeconds = 30);
    
    void StopMonitoring();
}
