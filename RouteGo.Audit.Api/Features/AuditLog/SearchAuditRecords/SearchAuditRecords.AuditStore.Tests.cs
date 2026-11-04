namespace RouteGo.Audit.Api.Features.AuditLog.SearchAuditRecords;

/// <summary>A refused search, read from its RFC 7807 body.</summary>
public sealed record SearchAuditRecordsProblem
{
    public required HttpStatusCode Status { get; init; }

    public string? Code { get; init; }

    /// <summary>The field names the problem's errors member names; empty when it has none.</summary>
    public required IReadOnlyList<string> ErrorFields { get; init; }
}

/// <summary>
/// DynamoDB Local in Docker with the audit table and its day index, the API host pointed at it,
/// and access tokens shaped exactly as RouteGo.Identity.Api issues them (RS256; sub, email, role),
/// signed with a key only this host trusts — so the tests exercise the real bearer validation
/// without an Identity instance to download keys from.
/// </summary>
public sealed class SearchAuditRecordsAuditStore : IAsyncLifetime
{
    // Process-wide values every host in the run sets identically.
    private const string TableName = "audit-log-search-tests";
    private const string Issuer = "http://localhost:5001";
    private const string Audience = "routego";

    private static readonly IClock _realClock = new Infrastructure.SystemClock();
    private static readonly RsaSecurityKey _trustedKey = CreateKey("audit-tests-trusted");
    private static readonly RsaSecurityKey _untrustedKey = CreateKey("audit-tests-untrusted");

    private readonly DynamoDbContainer _container = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Environment.SetEnvironmentVariable("AuditTableName", TableName);
        Environment.SetEnvironmentVariable("DynamoDbServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("SqsServiceUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("AWS_REGION", "ap-south-1");
        Environment.SetEnvironmentVariable("IdentityIssuer", Issuer);
        Environment.SetEnvironmentVariable("IdentityAudience", Audience);
        Environment.SetEnvironmentVariable("IdentityJwksUrl", "http://localhost:5001/.well-known/jwks.json");

        var config = new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await RecordAuditEvent.RecordAuditEventAuditStore.CreateAuditTableAsync(_dynamoDb, TableName);

        var auditLog = new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IAmazonDynamoDB), _dynamoDb);
        _factory = new ApiFactory(services => services.Add(auditLog), TrustTestSigningKey);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        _dynamoDb?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>A valid token for a person in the given role.</summary>
    public static string TokenFor(string role)
    {
        return Sign(role, _trustedKey);
    }

    /// <summary>A well-formed Admin token signed by a key Audit does not trust.</summary>
    public static string UntrustedAdminToken()
    {
        return Sign("Admin", _untrustedKey);
    }

    /// <summary>Writes one record the way the recording worker does, with details given as a stored map.</summary>
    public async Task PutRecordAsync(
        string entityType, string entityId, string eventId, DateTimeOffset occurredAt, Dictionary<string, AttributeValue>? details)
    {
        string sortKey = AuditKeys.SortKey(occurredAt, eventId);
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = AuditKeys.EntityPartitionKey(entityType, entityId) } },
            { "sk", new AttributeValue { S = sortKey } },
            { "gsi1pk", new AttributeValue { S = AuditKeys.BusinessDayKey(occurredAt) } },
            { "gsi1sk", new AttributeValue { S = sortKey } },
            { "eventId", new AttributeValue { S = eventId } },
            { "actorId", new AttributeValue { S = "meera-shah" } },
            { "actorRole", new AttributeValue { S = "Admin" } },
            { "action", new AttributeValue { S = "BookingForceCancelled" } },
            { "entityType", new AttributeValue { S = entityType } },
            { "entityId", new AttributeValue { S = entityId } },
            { "occurredAt", new AttributeValue { S = AuditKeys.FormatTime(occurredAt) } },
            { "correlationId", new AttributeValue { S = "corr-" + eventId } },
        };
        if (details != null)
        {
            item["details"] = new AttributeValue { M = details, IsMSet = true };
        }

        await _dynamoDb!.PutItemAsync(new PutItemRequest { TableName = TableName, Item = item });
    }

    /// <summary>Writes the given number of records for one entity, one minute apart from the start time.</summary>
    public async Task PutRecordsAsync(string entityType, string entityId, int count, DateTimeOffset firstAt)
    {
        for (int number = 0; number < count; number++)
        {
            await PutRecordAsync(entityType, entityId, $"EVT-{entityId}-{number:D3}", firstAt.AddMinutes(number), null);
        }
    }

    public async Task<HttpResponseMessage> SendAsync(string query, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/audit" + query);
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client!.SendAsync(request);
    }

    public async Task<SearchAuditRecordsResponse> SearchAsync(string query)
    {
        HttpResponseMessage response = await SendAsync(query, TokenFor("Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SearchAuditRecordsResponse? records = await response.Content.ReadFromJsonAsync<SearchAuditRecordsResponse>(WireJson.Options);
        return records!;
    }

    public async Task<SearchAuditRecordsProblem> GetProblemAsync(string query, string? accessToken)
    {
        HttpResponseMessage response = await SendAsync(query, accessToken);
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        var errorFields = new List<string>();
        if (root.TryGetProperty("errors", out JsonElement errors))
        {
            foreach (JsonProperty field in errors.EnumerateObject())
            {
                errorFields.Add(field.Name);
            }
        }

        return new SearchAuditRecordsProblem
        {
            Status = response.StatusCode,
            Code = ReadString(root, "code"),
            ErrorFields = errorFields,
        };
    }

    private static void TrustTestSigningKey(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(_trustedKey);
        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure<JwtBearerOptions>(
            services,
            JwtBearerDefaults.AuthenticationScheme,
            options => options.ConfigurationManager =
                new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    private static string Sign(string role, RsaSecurityKey key)
    {
        // The host checks expiry against real time with no grace, so tokens are minted against it too.
        DateTime now = _realClock.UtcNow.UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "person@example.com",
            ["role"] = role,
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static RsaSecurityKey CreateKey(string keyId)
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.GetString();
    }
}
