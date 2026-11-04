namespace RouteGo.Audit.Api.Features.AuditLog.RecordAuditEvent;

public sealed class RecordAuditEventTests : IClassFixture<RecordAuditEventAuditStore>
{
    private static readonly DateTimeOffset _tenFifteenOnSecondOctober = new DateTimeOffset(2026, 10, 2, 10, 15, 30, TimeSpan.Zero);

    private readonly RecordAuditEventAuditStore _store;

    public RecordAuditEventTests(RecordAuditEventAuditStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task HandleAsync_AdminForceCancelsBooking_WritesOneRecordWithWhoWhatWhichWhenAndWhy()
    {
        // Arrange
        string bookingId = NewBookingId();
        ReceivedEvent receivedEvent = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "BookingForceCancelled", "Operator breakdown"));

        // Act
        EventOutcome outcome = await _store.HandleAsync(receivedEvent);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        RecordAuditEventStoredRecord record = Assert.Single(await _store.ReadHistoryAsync("Booking", bookingId));
        Assert.Equal(receivedEvent.EventId, record.EventId);
        Assert.Equal("meera-shah", record.ActorId);
        Assert.Equal("Admin", record.ActorRole);
        Assert.Equal("BookingForceCancelled", record.Action);
        Assert.Equal("2026-10-02T10:15:30.0000000Z", record.OccurredAt);
        Assert.Equal(receivedEvent.CorrelationId, record.CorrelationId);
        Assert.Equal("Operator breakdown", record.Details!["reason"].S);
        Assert.Equal("100", record.Details["refundPercent"].N);
    }

    [Fact]
    public async Task HandleAsync_SameEventDeliveredFiveTimes_LeavesOneRecord()
    {
        // Arrange
        string bookingId = NewBookingId();
        ReceivedEvent receivedEvent = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "BookingForceCancelled", "Operator breakdown"));

        // Act
        EventOutcome first = await _store.HandleAsync(receivedEvent);
        EventOutcome second = await _store.HandleAsync(receivedEvent);
        EventOutcome third = await _store.HandleAsync(receivedEvent);
        EventOutcome fourth = await _store.HandleAsync(receivedEvent);
        EventOutcome fifth = await _store.HandleAsync(receivedEvent);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, first.Kind);
        Assert.Equal(EventOutcomeKind.Applied, second.Kind);
        Assert.Equal(EventOutcomeKind.Applied, third.Kind);
        Assert.Equal(EventOutcomeKind.Applied, fourth.Kind);
        Assert.Equal(EventOutcomeKind.Applied, fifth.Kind);
        Assert.Single(await _store.ReadHistoryAsync("Booking", bookingId));
    }

    [Fact]
    public async Task HandleAsync_RedeliveryWithDifferentReasonAndTime_KeepsTheFirstRecord()
    {
        // Arrange
        string bookingId = NewBookingId();
        string eventId = "EVT-" + Guid.NewGuid();
        ReceivedEvent original = RecordAuditEventAuditStore.AuditEvent(
            eventId, _tenFifteenOnSecondOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "OperatorApproved", "Documents verified"));
        ReceivedEvent changed = RecordAuditEventAuditStore.AuditEvent(
            eventId, _tenFifteenOnSecondOctober.AddHours(3),
            RecordAuditEventAuditStore.BookingData(bookingId, "OperatorApproved", "Changed my mind"));
        await _store.HandleAsync(original);

        // Act
        EventOutcome outcome = await _store.HandleAsync(changed);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        RecordAuditEventStoredRecord record = Assert.Single(await _store.ReadHistoryAsync("Booking", bookingId));
        Assert.Equal("Documents verified", record.Details!["reason"].S);
        Assert.Equal("2026-10-02T10:15:30.0000000Z", record.OccurredAt);
    }

    [Fact]
    public async Task HandleAsync_TwoActionsOnOneBooking_LeavesTwoRecords()
    {
        // Arrange
        string bookingId = NewBookingId();
        ReceivedEvent cancelled = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "BookingCancelled", "Customer request"));
        ReceivedEvent refunded = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober.AddMinutes(5),
            RecordAuditEventAuditStore.BookingData(bookingId, "RefundIssued", "Customer request"));

        // Act
        await _store.HandleAsync(cancelled);
        await _store.HandleAsync(refunded);

        // Assert
        List<RecordAuditEventStoredRecord> history = await _store.ReadHistoryAsync("Booking", bookingId);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task HandleAsync_LateEventArrivesLast_IsPlacedBeforeTheLaterAction()
    {
        // Arrange
        string bookingId = NewBookingId();
        ReceivedEvent laterAction = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober.AddMinutes(5),
            RecordAuditEventAuditStore.BookingData(bookingId, "RefundIssued", "Customer request"));
        ReceivedEvent earlierAction = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "BookingCancelled", "Customer request"));
        await _store.HandleAsync(laterAction);

        // Act
        await _store.HandleAsync(earlierAction);

        // Assert
        List<RecordAuditEventStoredRecord> history = await _store.ReadHistoryAsync("Booking", bookingId);
        Assert.Equal("BookingCancelled", history[0].Action);
        Assert.Equal("RefundIssued", history[1].Action);
    }

    [Fact]
    public async Task HandleAsync_ActionNameNeverSeenBefore_IsRecordedAsGiven()
    {
        // Arrange
        string bookingId = NewBookingId();
        ReceivedEvent receivedEvent = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "SeatBlockOverridden", "Coach swapped"));

        // Act
        EventOutcome outcome = await _store.HandleAsync(receivedEvent);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        RecordAuditEventStoredRecord record = Assert.Single(await _store.ReadHistoryAsync("Booking", bookingId));
        Assert.Equal("SeatBlockOverridden", record.Action);
    }

    [Fact]
    public async Task HandleAsync_EventNamesNoEntity_IsPostponedAsInvalidEventDataAndWritesNothing()
    {
        // Arrange
        string eventId = "EVT-" + Guid.NewGuid();
        ReceivedEvent receivedEvent = RecordAuditEventAuditStore.AuditEvent(
            eventId, _tenFifteenOnSecondOctober,
            "{\"actorId\":\"meera-shah\",\"actorRole\":\"Admin\",\"action\":\"BookingForceCancelled\"}");

        // Act
        EventOutcome outcome = await _store.HandleAsync(receivedEvent);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
        Assert.Contains("entityType", outcome.Problem);
        Assert.Contains("entityId", outcome.Problem);
        Assert.False(await _store.HasMarkerAsync(eventId));
    }

    [Fact]
    public async Task HandleAsync_AutomaticRefundBySystemActor_IsRecordedWithTheSystemRole()
    {
        // Arrange
        string paymentId = "P-" + Guid.NewGuid();
        string data = "{\"actorId\":\"payment-service\",\"actorRole\":\"System\",\"action\":\"RefundIssued\","
            + "\"entityType\":\"Payment\",\"entityId\":\"" + paymentId + "\","
            + "\"details\":{\"bookingId\":\"B-82\",\"amount\":900,\"reason\":\"LatePayment\"}}";
        ReceivedEvent receivedEvent = RecordAuditEventAuditStore.AuditEvent("EVT-" + Guid.NewGuid(), _tenFifteenOnSecondOctober, data);

        // Act
        await _store.HandleAsync(receivedEvent);

        // Assert
        RecordAuditEventStoredRecord record = Assert.Single(await _store.ReadHistoryAsync("Payment", paymentId));
        Assert.Equal("payment-service", record.ActorId);
        Assert.Equal("System", record.ActorRole);
        Assert.Equal("RefundIssued", record.Action);
        Assert.Equal("B-82", record.Details!["bookingId"].S);
        Assert.Equal("900", record.Details["amount"].N);
        Assert.Equal("LatePayment", record.Details["reason"].S);
    }

    [Fact]
    public async Task HandleAsync_ActionJustAfterMidnightIndiaTime_IsFiledUnderTheNewBusinessDay()
    {
        // Arrange
        string bookingId = NewBookingId();
        var nineteenHundredUtcOnFirstOctober = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
        ReceivedEvent receivedEvent = RecordAuditEventAuditStore.AuditEvent(
            "EVT-" + Guid.NewGuid(), nineteenHundredUtcOnFirstOctober,
            RecordAuditEventAuditStore.BookingData(bookingId, "BookingForceCancelled", "Operator breakdown"));

        // Act
        await _store.HandleAsync(receivedEvent);

        // Assert
        RecordAuditEventStoredRecord record = Assert.Single(await _store.ReadHistoryAsync("Booking", bookingId));
        Assert.Equal("DAY#2026-10-02", record.DayKey);
    }

    private static string NewBookingId()
    {
        return "BKG-" + Guid.NewGuid();
    }
}
