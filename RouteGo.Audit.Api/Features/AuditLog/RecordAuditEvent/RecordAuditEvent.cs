namespace RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;

/// <summary>
/// The data of an audit event as a publisher sends it: who did what to which thing, and why. Every
/// text is kept exactly as given — an action or entity kind Audit has not seen before is recorded,
/// not refused. A field a publisher left out arrives as null and is refused by the validator.
/// </summary>
public sealed record AuditEventMessage
{
    /// <summary>Who performed the action: an account identifier, or a name where no account acted.</summary>
    public string? ActorId { get; init; }

    /// <summary>The role the actor acted in, for example Admin or System.</summary>
    public string? ActorRole { get; init; }

    /// <summary>What was done, for example BookingForceCancelled.</summary>
    public string? Action { get; init; }

    /// <summary>The kind of thing the action was done to, for example Booking.</summary>
    public string? EntityType { get; init; }

    /// <summary>The identifier of the thing the action was done to.</summary>
    public string? EntityId { get; init; }

    /// <summary>The reason and any figures that explain the action; null when the publisher gave none.</summary>
    public JsonElement? Details { get; init; }
}

/// <summary>Checks that an audit event names its actor, role, action and entity before anything is written.</summary>
public sealed class AuditEventMessageValidator : AbstractValidator<AuditEventMessage>
{
    public AuditEventMessageValidator()
    {
        RuleFor(x => x.ActorId).NotEmpty().WithMessage("actorId is missing.");
        RuleFor(x => x.ActorRole).NotEmpty().WithMessage("actorRole is missing.");
        RuleFor(x => x.Action).NotEmpty().WithMessage("action is missing.");
        RuleFor(x => x.EntityType).NotEmpty().WithMessage("entityType is missing.");
        RuleFor(x => x.EntityId).NotEmpty().WithMessage("entityId is missing.");
        RuleFor(x => x.Details).Must(BeAnObjectWhenGiven).WithMessage("details must be a JSON object.");
    }

    private static bool BeAnObjectWhenGiven(JsonElement? details)
    {
        if (details is null)
        {
            return true;
        }

        return details.Value.ValueKind == JsonValueKind.Object;
    }
}

/// <summary>
/// Writes the permanent record of one audit event. Repeat safety: the event's identity is the key.
/// A marker item for the event and the record are written together, each only when it does not
/// exist yet, so a second delivery — even one whose content or time differs — finds the marker,
/// changes nothing and counts as recorded.
/// </summary>
public sealed partial class RecordAuditEventHandler
{
    private const string ConditionalCheckFailed = "ConditionalCheckFailed";
    private const string NotYetWritten = "attribute_not_exists(pk)";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly ILogger<RecordAuditEventHandler> _logger;
    private readonly AuditEventMessageValidator _validator = new();

    public RecordAuditEventHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration, ILogger<RecordAuditEventHandler> logger)
    {
        _dynamoDb = dynamoDb;
        // Present by the time the worker runs: startup refuses to run without it.
        _tableName = configuration[Infrastructure.ConfigKeys.AuditTableName]!;
        _logger = logger;
    }

    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        AuditEventMessage? message = ReadMessage(receivedEvent);
        if (message is null)
        {
            return EventOutcome.Postponed(EventOutcome.InvalidEventData, "the event data is not an audit event");
        }

        ValidationResult validation = await _validator.ValidateAsync(message, cancellationToken);
        if (!validation.IsValid)
        {
            // Retried and then parked, never guessed at: a record with a missing fact is worse than a gap that is visible.
            return EventOutcome.Postponed(EventOutcome.InvalidEventData, DescribeProblems(validation));
        }

        bool isNew = await WriteOnceAsync(receivedEvent, message, cancellationToken);
        LogOutcome(receivedEvent, message, isNew);
        return EventOutcome.Applied();
    }

    private static AuditEventMessage? ReadMessage(ReceivedEvent receivedEvent)
    {
        try
        {
            return receivedEvent.Data.Deserialize<AuditEventMessage>(WireJson.Options);
        }
        // A field of the wrong kind cannot be read; the caller reports the event as unusable.
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeProblems(ValidationResult validation)
    {
        IEnumerable<string> messages = validation.Errors.Select(error => error.ErrorMessage);
        return string.Join(" ", messages);
    }

    // True when this call wrote the record, false when the event had been recorded before.
    private async Task<bool> WriteOnceAsync(ReceivedEvent receivedEvent, AuditEventMessage message, CancellationToken cancellationToken)
    {
        var request = new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                new TransactWriteItem { Put = NewPut(ToMarkerItem(receivedEvent)) },
                new TransactWriteItem { Put = NewPut(ToRecordItem(receivedEvent, message)) },
            },
        };

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(request, cancellationToken);
            return true;
        }
        // A refused condition is the marker (or record) already being there: the event was recorded before.
        catch (TransactionCanceledException exception) when (IsAlreadyWritten(exception))
        {
            return false;
        }
    }

    private static bool IsAlreadyWritten(TransactionCanceledException exception)
    {
        if (exception.CancellationReasons is null)
        {
            return false;
        }

        return exception.CancellationReasons.Exists(reason => reason.Code == ConditionalCheckFailed);
    }

    private Put NewPut(Dictionary<string, AttributeValue> item)
    {
        return new Put { TableName = _tableName, Item = item, ConditionExpression = NotYetWritten };
    }

    private void LogOutcome(ReceivedEvent receivedEvent, AuditEventMessage message, bool isNew)
    {
        if (isNew)
        {
            _logger.LogInformation(
                "Recorded {Action} on {EntityType} {EntityId} from event {EventId} (correlation {CorrelationId})",
                message.Action, message.EntityType, message.EntityId, receivedEvent.EventId, receivedEvent.CorrelationId);
            return;
        }

        _logger.LogInformation(
            "Audit event {EventId} was already recorded; nothing changed (correlation {CorrelationId})",
            receivedEvent.EventId, receivedEvent.CorrelationId);
    }
}
