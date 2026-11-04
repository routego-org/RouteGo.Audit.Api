using RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;

namespace RouteGo.Audit.Api.Infrastructure.Messaging;

/// <summary>A log that keeps what the worker wrote, so a test can read the alert line an operations user would see.</summary>
public sealed class AuditEventsServiceLog : ILogger<AuditEventsService>
{
    private readonly List<string> _errors = new List<string>();

    public IReadOnlyList<string> Errors
    {
        get
        {
            return _errors;
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Error)
        {
            _errors.Add(formatter(state, exception));
        }
    }
}

/// <summary>
/// DynamoDB Local with the audit table and LocalStack with the audit queue (its dead-letter queue
/// and a one-second visibility timeout, so a retry does not wait half a minute). The worker under
/// test is the real one, reading the real queue and writing the real table.
/// </summary>
public sealed class AuditEventsServiceAuditStore : IAsyncLifetime
{
    private const string TableName = "audit-log-queue-tests";
    private const string QueueName = "audit-q-tests";
    private const string DeadLetterQueueName = "audit-dlq-tests";

    private readonly DynamoDbContainer _dynamoDbContainer = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.4").Build();
    private readonly AuditEventsServiceLog _log = new AuditEventsServiceLog();
    private AmazonDynamoDBClient? _dynamoDb;
    private AmazonSQSClient? _sqs;
    private AuditEventsService? _service;
    private string _queueUrl = string.Empty;

    public AuditEventsServiceLog Log
    {
        get
        {
            return _log;
        }
    }

    public async Task InitializeAsync()
    {
        await _dynamoDbContainer.StartAsync();
        await _localStack.StartAsync();

        var dynamoDbConfig = new AmazonDynamoDBConfig { ServiceURL = _dynamoDbContainer.GetConnectionString() };
        _dynamoDb = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), dynamoDbConfig);
        await Features.AuditLog.RecordAuditEvent.RecordAuditEventAuditStore.CreateAuditTableAsync(_dynamoDb, TableName);

        var sqsConfig = new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString(), AuthenticationRegion = "us-east-1" };
        _sqs = new AmazonSQSClient(new Amazon.Runtime.BasicAWSCredentials("local", "local"), sqsConfig);
        string deadLetterQueueUrl = await CreateQueuesAsync(_sqs);

        IConfiguration settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "AuditTableName", TableName },
                { "AuditEventsQueueUrl", _queueUrl },
                { "AuditEventsDeadLetterQueueUrl", deadLetterQueueUrl },
            })
            .Build();
        var handler = new RecordAuditEventHandler(_dynamoDb, settings, NullLogger<RecordAuditEventHandler>.Instance);
        _service = new AuditEventsService(handler, _sqs, settings, _log);
    }

    public async Task DisposeAsync()
    {
        _sqs?.Dispose();
        _dynamoDb?.Dispose();
        await _localStack.DisposeAsync();
        await _dynamoDbContainer.DisposeAsync();
    }

    /// <summary>An audit event as the topic delivers it to the queue: the envelope around the publisher's data.</summary>
    public static string Envelope(string eventId, DateTimeOffset occurredAt, string dataJson)
    {
        return "{\"eventId\":\"" + eventId + "\",\"eventType\":\"AuditRecorded\",\"version\":1,\"occurredAt\":\""
            + AuditKeys.FormatTime(occurredAt) + "\",\"correlationId\":\"corr-" + eventId
            + "\",\"producer\":\"booking-service\",\"data\":" + dataJson + "}";
    }

    public async Task SendAsync(string body)
    {
        await _sqs!.SendMessageAsync(_queueUrl, body);
    }

    /// <summary>Puts one audit event per minute on the queue, each a different action on the same booking.</summary>
    public async Task SendActionsAsync(string bookingId, int count, DateTimeOffset firstAt)
    {
        for (int number = 0; number < count; number++)
        {
            string eventId = "EVT-" + Guid.NewGuid();
            string data = Features.AuditLog.RecordAuditEvent.RecordAuditEventAuditStore.BookingData(
                bookingId, "Action" + number, "Reason " + number);
            await SendAsync(Envelope(eventId, firstAt.AddMinutes(number), data));
        }
    }

    /// <summary>Lets the worker read the queue until it has applied the given number of events.</summary>
    public async Task<int> ApplyEventsAsync(int expected)
    {
        int applied = 0;
        int rounds = 0;
        while (applied < expected && rounds < 10)
        {
            applied += await _service!.HandleWaitingEventsAsync(CancellationToken.None);
            rounds++;
        }

        return applied;
    }

    /// <summary>Lets the worker read the queue a fixed number of times, whatever it finds.</summary>
    public async Task ReadRoundsAsync(int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            await _service!.HandleWaitingEventsAsync(CancellationToken.None);
        }
    }

    public async Task<int> CountRecordsAsync(string entityType, string entityId)
    {
        var query = new QueryRequest
        {
            TableName = TableName,
            KeyConditionExpression = "pk = :pk",
            Select = Select.COUNT,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue { S = AuditKeys.EntityPartitionKey(entityType, entityId) } },
            },
        };
        QueryResponse response = await _dynamoDb!.QueryAsync(query);
        return response.Count ?? 0;
    }

    // Returns the dead-letter queue's address; the audit queue's is kept for the worker and the tests.
    private async Task<string> CreateQueuesAsync(AmazonSQSClient sqs)
    {
        CreateQueueResponse deadLetter = await sqs.CreateQueueAsync(DeadLetterQueueName);
        GetQueueAttributesResponse attributes = await sqs.GetQueueAttributesAsync(
            deadLetter.QueueUrl, new List<string> { "QueueArn" });
        string redrivePolicy = "{\"deadLetterTargetArn\":\"" + attributes.QueueARN + "\",\"maxReceiveCount\":\"5\"}";

        var queue = new CreateQueueRequest
        {
            QueueName = QueueName,
            Attributes = new Dictionary<string, string>
            {
                { "VisibilityTimeout", "1" },
                { "RedrivePolicy", redrivePolicy },
            },
        };
        CreateQueueResponse created = await sqs.CreateQueueAsync(queue);
        _queueUrl = created.QueueUrl;
        return deadLetter.QueueUrl;
    }
}
