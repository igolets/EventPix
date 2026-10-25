using EventPix.Web.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventPix.Web.Health;

/// <summary>
/// Readiness probe: the SQLite database opens and answers. Returns a status only —
/// no event, member or storage details ever leave this endpoint.
/// </summary>
internal sealed class DbReadyHealthCheck(EventPixDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        return await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database unreachable.");
    }
}
