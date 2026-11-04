using System.Diagnostics;
using System.Text.Json.Serialization;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using RouteGo.Audit.Api.Infrastructure;
using RouteGo.Audit.Api.Observability;
// FILLED BY the sibling slice skill — the usings for what it wires below (e.g. using RouteGo.Audit.Api.Infrastructure.Pgsql;
// when the first data slice lands). Non-System usings only: System.* sort first (IDE0055).
using Amazon.Runtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;
using RouteGo.Audit.Api.Infrastructure.Messaging;

// Configuration reaches this app through ONE channel in every environment: environment variables,
// flat-named so there is no ':' / '__' separator to mistype (Infrastructure/ConfigKeys.cs). The
// deployed values come from the deployment template; the local ones from LocalEntryPoints, which
// MUST run before the builder is created — its environment-variable provider snapshots the process
// block once, so anything set after that line never reaches IConfiguration
// (references/configuration.md).
LocalEntryPoints.Apply();
// FILLED BY the sibling slice skill when the first data slice lands — BEFORE the builder, so nothing
// can query first: Dapper bakes its name mapping per (query, type) at first execution, so a late call
// is a silent no-op (skills-db-postgres #11/#25; its check_dapper_mapping.py D3 anchors on this line).
// The timeout is DERIVED from this API's caller deadline, with the derivation in a why-comment here:
//   DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(...));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// DI scope validation, on in EVERY environment (skills-db-postgres #26). CreateBuilder turns
// these on in Development only, so a captive dependency throws on a laptop and goes unnoticed in
// production: a singleton that takes a scoped IWriterConnectionFactory or ICurrentActor resolves
// it from the ROOT scope and freezes the first request's actor for the life of the process, and
// every later user's writes are stamped with that identity. ValidateOnBuild catches the capture
// at startup; ValidateScopes catches a scoped service pulled from app.Services at runtime.
// A startup failure here is the finding, not a regression - read the exception, fix the capture.
builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Fail at startup naming every missing key at once, rather than on whichever request first needs
// one. List only what the app cannot start without; a legitimately optional key is left out.
// FILLED BY the sibling slice skill as it introduces keys:
//   builder.Configuration.RequireKeys(ConfigKeys.BackendBaseUrl, ConfigKeys.ClientId);
builder.Configuration.RequireKeys(
    ConfigKeys.AuditTableName, ConfigKeys.AuditEventsQueueUrl, ConfigKeys.AuditEventsDeadLetterQueueUrl,
    ConfigKeys.IdentityIssuer, ConfigKeys.IdentityAudience, ConfigKeys.IdentityJwksUrl);

// Log levels, in code for the same reason every other setting is: one place, and no appsettings
// file to disagree with it. Everything defaults to Information; the framework's own request/routing
// chatter stays at Warning so the telemetry pipeline is not flooded, except in Development where it
// is the most useful thing in the console. A deployed run can still be turned up without a code
// change: Logging__LogLevel__Default is an ordinary environment variable on the function.
LogLevel frameworkLogLevel = LogLevel.Warning;
if (builder.Environment.IsDevelopment())
{
    frameworkLogLevel = LogLevel.Information;
}

builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkLogLevel);

// LMI hosting — no-op outside Lambda (references/lambda-hosting.md).
builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);

// Secrets: standard AWS Secrets Manager access (references/secrets.md). ONE fixed name per secret;
// the SOURCE is the environment — Development reads configuration, everything else Secrets Manager.
builder.Services.AddSingleton<SecretResolver>();

// Wire naming contract (references/host-components.md): camelCase properties are the web
// default; enums serialize as camelCase STRINGS (without this converter they serialize as
// numbers and every enum in every response breaks the authored contract). Same policy as
// Common/WireJson.cs, which covers HttpClient reads and hand-rolled serialization.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Observability: vendor-neutral OpenTelemetry (traces + logs) over OTLP — baked in
// (references/observability.md). Deployed Lambda -> ADOT Collector on localhost:4318; local dev ->
// Otel:Exporter:* from user-secrets; unset -> graceful no-op. Flush on shutdown is automatic.
builder.Services.AddObservability(
    builder.Configuration, builder.Environment, builder.Logging,
    configureTracing: tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
        });
        tracing.AddProcessor<PiiRedactingProcessor>();
        // FILLED BY the sibling slice skill when the first data slice lands — the database driver's
        // spans, subscribed by SOURCE NAME so this file keeps no driver reference and the architecture
        // test's DB-driver rule holds (skills-db-postgres #20):
        //   tracing.AddSource("Npgsql");
    });

// Development only: OTel records the request line and the response status of every outbound call
// but never the bodies, so a 4xx from a dependency reads as a bare status code in the dashboard.
// This puts the payloads on the enclosing span as an "http.payload" event — see
// HttpPayloadDiagnosticsHandler for what it declines to read and why successful responses are
// left out. ConfigureHttpClientDefaults reaches every IHttpClientFactory client at once, so no
// individual client registration has to know about it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddTransient<HttpPayloadDiagnosticsHandler>();
    builder.Services.ConfigureHttpClientDefaults(
        http => http.AddHttpMessageHandler<HttpPayloadDiagnosticsHandler>());
}

// Errors: RFC 7807 ProblemDetails + unhandled-exception handler (references/host-components.md).
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails =
    c => c.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString());
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// Infrastructure ports — registered once. No per-slice registrations.
builder.Services.AddSingleton<IClock, SystemClock>();
// FILLED BY skills-aspnetcore-api-vertical-slice when the first data slice lands:
//   the reader/writer connection-port registrations (files copied from skills-db-postgres —
//   reader singleton; writer scoped, it carries the per-request actor) (+ any other ports).
// The audit log is DynamoDB. Deployed, the client uses the regional endpoint and the instance
// role; with DynamoDbServiceUrl set (DynamoDB Local on a laptop or in tests) it talks to that
// endpoint with placeholder credentials, so a developer needs no AWS profile to run the API.
var dynamoDbConfig = new AmazonDynamoDBConfig();
string? dynamoDbServiceUrl = builder.Configuration[ConfigKeys.DynamoDbServiceUrl];
AmazonDynamoDBClient dynamoDbClient;
if (string.IsNullOrWhiteSpace(dynamoDbServiceUrl))
{
    dynamoDbClient = new AmazonDynamoDBClient(dynamoDbConfig);
}
else
{
    dynamoDbConfig.ServiceURL = dynamoDbServiceUrl;
    dynamoDbClient = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), dynamoDbConfig);
}
builder.Services.AddSingleton<IAmazonDynamoDB>(dynamoDbClient);
// The audit queue is read through SQS, reached the same way: LocalStack with placeholder
// credentials when SqsServiceUrl is set.
var sqsConfig = new AmazonSQSConfig();
string? sqsServiceUrl = builder.Configuration[ConfigKeys.SqsServiceUrl];
AmazonSQSClient sqsClient;
if (string.IsNullOrWhiteSpace(sqsServiceUrl))
{
    sqsClient = new AmazonSQSClient(sqsConfig);
}
else
{
    sqsConfig.ServiceURL = sqsServiceUrl;
    sqsClient = new AmazonSQSClient(new BasicAWSCredentials("local", "local"), sqsConfig);
}
builder.Services.AddSingleton<IAmazonSQS>(sqsClient);
// The queue worker runs inside this API, on every running copy of it; a queue hands each message
// to one copy only, so the copies need no coordination.
builder.Services.AddSingleton<RecordAuditEventHandler>();
builder.Services.AddHostedService<AuditEventsService>();
// Readiness reports whether the audit table can be reached; liveness (/health) never touches it.
builder.Services.AddHealthChecks().AddCheck<AuditTableHealthCheck>("audit-table");
// Access tokens are issued by RouteGo.Identity.Api and checked here exactly as the other services
// check them: RS256 signature against Identity's published keys, issuer, audience, and exact
// expiry with no extra minutes of grace. Claim names stay as issued (sub, email, role,
// operatorId). Identity is reached over RouteGo's private network on plain HTTP, so the key
// download cannot insist on HTTPS. The framework adds the authentication and authorization
// middleware itself once these services are registered.
var identitySigningKeys = new ConfigurationManager<OpenIdConnectConfiguration>(
    builder.Configuration[ConfigKeys.IdentityJwksUrl]!,
    new IdentityKeySetRetriever(),
    new HttpDocumentRetriever { RequireHttps = false });
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.ConfigurationManager = identitySigningKeys;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = builder.Configuration[ConfigKeys.IdentityIssuer],
        ValidAudience = builder.Configuration[ConfigKeys.IdentityAudience],
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        NameClaimType = TokenClaims.Email,
        RoleClaimType = TokenClaims.Role,
        ClockSkew = TimeSpan.Zero,
    };
    options.Events = new JwtBearerEvents
    {
        OnChallenge = AuthenticationProblems.WriteUnauthorizedAsync,
        OnForbidden = AuthenticationProblems.WriteForbiddenAsync,
    };
});
builder.Services.AddAuthorization();

// OpenAPI: one document per Feature Area, from the explicit list (references/host-components.md).
// new_slice.py appends an area string here when a NEW area's first slice lands.
string[] areas = { "AuditLog" };
foreach (string area in areas)
{
    builder.Services.AddOpenApi(area, o =>
    {
        string areaName = area;
        o.ShouldInclude = api =>
            api.GroupName is null || string.Equals(api.GroupName, areaName, StringComparison.OrdinalIgnoreCase);
        o.AddOperationTransformer<ResponseHeadersTransformer>();
    });
}

WebApplication app = builder.Build();

app.UseMiddleware<RequestTelemetryMiddleware>(); // OUTERMOST — identity + request logging; it ENRICHES the instrumentation's span (references/observability.md)
app.UseExceptionHandler();                       // unhandled -> RFC 7807 problem+json
app.UseMiddleware<ResponseMetadataMiddleware>(); // X-Trace-Id, Server-Timing, ... on every response
app.MapControllers();
app.MapOpenApi();                                // /openapi/{area}.json per document
if (app.Environment.IsDevelopment())             // dev-only — the doc is not the published contract
{
    app.MapScalarApiReference(o =>
    {
        o.WithTitle("API Reference");
        for (int i = 0; i < areas.Length; i++)
        {
            o.AddDocument(areas[i], areas[i], $"/openapi/{areas[i]}.json", isDefault: i == 0);
        }
    });
    app.MapGet("/", () => Results.Redirect("/scalar")); // one URL to remember
}
app.MapGet("/health", () => Results.Ok("healthy")); // placeholder until the first slice lands
app.MapHealthChecks("/health/ready");               // readiness: the audit table answers

app.Run();

public partial class Program { } // exposes Program to WebApplicationFactory<Program> in tests
