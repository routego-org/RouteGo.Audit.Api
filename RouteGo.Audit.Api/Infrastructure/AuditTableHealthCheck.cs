using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RouteGo.Audit.Api.Infrastructure;

/// <summary>
/// Reports whether the audit table answers. It reads one key that is never written, because the
/// service's role may only put, get and query: a table description would be refused and would
/// report a healthy table as down.
/// </summary>
public sealed class AuditTableHealthCheck : IHealthCheck
{
    private const string ProbePartitionKey = "HEALTH#probe";
    private const string ProbeSortKey = "PROBE";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public AuditTableHealthCheck(IAmazonDynamoDB dynamoDb, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _tableName = configuration[ConfigKeys.AuditTableName]!;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var key = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = ProbePartitionKey } },
            { "sk", new AttributeValue { S = ProbeSortKey } },
        };

        try
        {
            await _dynamoDb.GetItemAsync(_tableName, key, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        // The health endpoint's whole job is to turn any failure into an answer.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"The audit table '{_tableName}' could not be read.", exception);
        }
    }
}
