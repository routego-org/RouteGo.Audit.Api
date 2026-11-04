namespace RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;

/// <summary>An audit record as the audit log holds it, read back raw.</summary>
public sealed record RecordAuditEventStoredRecord
{
    public required string EventId { get; init; }

    public required string ActorId { get; init; }

    public required string ActorRole { get; init; }

    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string OccurredAt { get; init; }

    public required string DayKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The details map as stored; null when the event gave none.</summary>
    public Dictionary<string, AttributeValue>? Details { get; init; }
}

/// <summary>
/// DynamoDB Local in Docker with the audit table and its day index, and the handler under test
/// pointed at it. Events are built the way publishers send them and records are read back raw,
/// so each test asserts what is really stored.
/// </summary>
public sealed class RecordAuditEventAuditStore : IAsyncLifetime
{
    public const string TableName = "audit-log-record-tests";

    private readonly DynamoDbContainer _container = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private AmazonDynamoDBClient? _dynamoDb;
    private RecordAuditEventHandler? _handler;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var config = new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), config);
        await CreateAuditTableAsync(_dynamoDb, TableName);

        IConfiguration settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "AuditTableName", TableName } })
            .Build();
        _handler = new RecordAuditEventHandler(_dynamoDb, settings, NullLogger<RecordAuditEventHandler>.Instance);
    }

    public async Task DisposeAsync()
    {
        _dynamoDb?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>An audit event as it arrives on the queue: the envelope facts plus the publisher's data.</summary>
    public static ReceivedEvent AuditEvent(string eventId, DateTimeOffset occurredAt, string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new ReceivedEvent
        {
            EventId = eventId,
            EventType = "AuditRecorded",
            OccurredAt = occurredAt,
            CorrelationId = "corr-" + eventId,
            Data = document.RootElement.Clone(),
        };
    }

    /// <summary>The data of an audit event on a booking, with a reason and a refund percentage.</summary>
    public static string BookingData(string bookingId, string action, string reason)
    {
        return "{\"actorId\":\"meera-shah\",\"actorRole\":\"Admin\",\"action\":\"" + action
            + "\",\"entityType\":\"Booking\",\"entityId\":\"" + bookingId
            + "\",\"details\":{\"reason\":\"" + reason + "\",\"refundPercent\":100}}";
    }

    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent)
    {
        return await _handler!.HandleAsync(receivedEvent, CancellationToken.None);
    }

    /// <summary>Everything the log holds for one entity, in the order the table returns it.</summary>
    public async Task<List<RecordAuditEventStoredRecord>> ReadHistoryAsync(string entityType, string entityId)
    {
        var query = new QueryRequest
        {
            TableName = TableName,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = AuditKeys.EntityPartitionKey(entityType, entityId) } },
            },
        };
        QueryResponse response = await _dynamoDb!.QueryAsync(query);
        return response.Items.Select(ToStoredRecord).ToList();
    }

    /// <summary>Whether the marker for an event's identity exists.</summary>
    public async Task<bool> HasMarkerAsync(string eventId)
    {
        var key = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = AuditKeys.EventPartitionKey(eventId) } },
            { "sk", new AttributeValue { S = AuditKeys.EventMarkerSortKey } },
        };
        GetItemResponse response = await _dynamoDb!.GetItemAsync(TableName, key);
        return response.IsItemSet;
    }

    /// <summary>Creates the audit table and its day index exactly as the deployment does.</summary>
    public static async Task CreateAuditTableAsync(AmazonDynamoDBClient dynamoDb, string tableName)
    {
        var request = new CreateTableRequest
        {
            TableName = tableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new AttributeDefinition { AttributeName = "pk", AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = "sk", AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = "gsi1pk", AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = "gsi1sk", AttributeType = ScalarAttributeType.S },
            },
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = "pk", KeyType = KeyType.HASH },
                new KeySchemaElement { AttributeName = "sk", KeyType = KeyType.RANGE },
            },
            GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
            {
                new GlobalSecondaryIndex
                {
                    IndexName = AuditKeys.DayIndexName,
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement { AttributeName = "gsi1pk", KeyType = KeyType.HASH },
                        new KeySchemaElement { AttributeName = "gsi1sk", KeyType = KeyType.RANGE },
                    },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            },
        };
        await dynamoDb.CreateTableAsync(request);
    }

    private static RecordAuditEventStoredRecord ToStoredRecord(Dictionary<string, AttributeValue> item)
    {
        Dictionary<string, AttributeValue>? details = null;
        if (item.ContainsKey("details"))
        {
            details = item["details"].M;
        }

        return new RecordAuditEventStoredRecord
        {
            EventId = item["eventId"].S,
            ActorId = item["actorId"].S,
            ActorRole = item["actorRole"].S,
            Action = item["action"].S,
            EntityType = item["entityType"].S,
            EntityId = item["entityId"].S,
            OccurredAt = item["occurredAt"].S,
            DayKey = item["gsi1pk"].S,
            CorrelationId = item["correlationId"].S,
            Details = details,
        };
    }
}
