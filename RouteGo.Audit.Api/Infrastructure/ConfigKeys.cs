namespace RouteGo.Audit.Api.Infrastructure;

/// <summary>
/// The name of every environment-supplied configuration value, spelled exactly once. The names are
/// deliberately FLAT: each maps 1:1 to an environment variable of the same spelling, so no ':' or
/// '__' separator can be mistyped between the deployment template, the pipeline, and the reader
/// here. LocalEntryPoints and every consumer go through these constants, so a rename is a compile
/// error rather than a value that silently arrives null (references/configuration.md).
/// </summary>
public static class ConfigKeys
{
    /// <summary>The AWS SDK's own region variable, and the ONLY region source the app reads.
    /// Lambda sets it; LocalEntryPoints seeds it on a laptop. There is deliberately no app-owned
    /// AWS:Region key that could disagree with the runtime's own value.</summary>
    public const string AwsRegion = "AWS_REGION";

    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per value
    // that varies by environment — a base URL, a client id, a public origin.
    //
    //   public const string BackendBaseUrl = "BackendBaseUrl";
    //
    // Adding one is four more edits, none of them a new file: a local value in
    // LocalEntryPoints, a parameter + env var in the deployment template, a
    // pipeline variable, and a RequireKeys entry if the app cannot start without
    // it. NEVER add a configuration key to an appsettings*.json.
    //
    // Otel:Exporter:* and AWS:Profile do NOT belong here — see
    // references/configuration.md for why those two keep their nested names.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The DynamoDB table holding the audit log (routego-audit-log).</summary>
    public const string AuditTableName = "AuditTableName";

    /// <summary>The queue the audit events arrive on (audit-q).</summary>
    public const string AuditEventsQueueUrl = "AuditEventsQueueUrl";

    /// <summary>Where an audit event that can never be recorded is set aside at once (audit-dlq).</summary>
    public const string AuditEventsDeadLetterQueueUrl = "AuditEventsDeadLetterQueueUrl";

    /// <summary>The issuer Identity puts in every access token.</summary>
    public const string IdentityIssuer = "IdentityIssuer";

    /// <summary>The audience Identity puts in every access token.</summary>
    public const string IdentityAudience = "IdentityAudience";

    /// <summary>Where Identity publishes the keys its tokens are signed with.</summary>
    public const string IdentityJwksUrl = "IdentityJwksUrl";

    /// <summary>A DynamoDB endpoint to use instead of AWS (DynamoDB Local); unset when deployed.</summary>
    public const string DynamoDbServiceUrl = "DynamoDbServiceUrl";

    /// <summary>An SQS endpoint to use instead of AWS (LocalStack); unset when deployed.</summary>
    public const string SqsServiceUrl = "SqsServiceUrl";
}
