using RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;

namespace RouteGo.Audit.Api.Infrastructure.Messaging;

/// <summary>
/// Waits on the audit queue — subscribed to the audit topic, letting only AuditRecorded through —
/// and records each event it receives. There is one event type and one slice, so the queue's
/// worker hands every event straight to that slice's handler.
/// </summary>
public sealed class AuditEventsService : EventQueueService
{
    private const string AuditRecordedEventType = "AuditRecorded";

    private static readonly string[] _eventTypes = new string[] { AuditRecordedEventType };

    private readonly RecordAuditEventHandler _handler;

    public AuditEventsService(
        RecordAuditEventHandler handler, IAmazonSQS sqs, IConfiguration configuration, ILogger<AuditEventsService> logger)
        : base(sqs, QueueOf(configuration), logger)
    {
        _handler = handler;
    }

    protected override async Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        return await _handler.HandleAsync(receivedEvent, cancellationToken);
    }

    private static EventQueue QueueOf(IConfiguration configuration)
    {
        // Present by the time the worker runs: startup refuses to run without them.
        return new EventQueue
        {
            QueueUrl = configuration[ConfigKeys.AuditEventsQueueUrl]!,
            DeadLetterQueueUrl = configuration[ConfigKeys.AuditEventsDeadLetterQueueUrl]!,
            EventTypes = _eventTypes,
            NotAppliedProblem = ProblemTypes.AuditEventParked,
        };
    }
}
