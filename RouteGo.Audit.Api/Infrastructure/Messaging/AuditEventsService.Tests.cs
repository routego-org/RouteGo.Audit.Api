namespace RouteGo.Audit.Api.Infrastructure.Messaging;

public sealed class AuditEventsServiceTests : IClassFixture<AuditEventsServiceAuditStore>
{
    private static readonly DateTimeOffset _nineOnSecondOctober = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly AuditEventsServiceAuditStore _store;

    public AuditEventsServiceTests(AuditEventsServiceAuditStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task HandleWaitingEventsAsync_TwentyEventsWaitedWhileStopped_RecordsEachOnce()
    {
        // Arrange
        string bookingId = "BKG-" + Guid.NewGuid();
        await _store.SendActionsAsync(bookingId, 20, _nineOnSecondOctober);

        // Act
        int applied = await _store.ApplyEventsAsync(20);

        // Assert
        Assert.Equal(20, applied);
        Assert.Equal(20, await _store.CountRecordsAsync("Booking", bookingId));
    }

    [Fact]
    public async Task HandleWaitingEventsAsync_EventMissingItsEntity_IsRetriedThenParkedWithAnAlertNamingItWhileTheOthersAreRecorded()
    {
        // Arrange
        string brokenEventId = "EVT-" + Guid.NewGuid();
        string bookingId = "BKG-" + Guid.NewGuid();
        string brokenData = "{\"actorId\":\"meera-shah\",\"actorRole\":\"Admin\",\"action\":\"BookingForceCancelled\"}";
        string goodData = Features.AuditLog.RecordAuditEvent.RecordAuditEventAuditStore.BookingData(bookingId, "BookingCancelled", "Customer request");
        await _store.SendAsync(AuditEventsServiceAuditStore.Envelope(brokenEventId, _nineOnSecondOctober, brokenData));
        await _store.SendAsync(AuditEventsServiceAuditStore.Envelope("EVT-" + Guid.NewGuid(), _nineOnSecondOctober, goodData));

        // Act
        await _store.ReadRoundsAsync(5);

        // Assert
        Assert.Equal(1, await _store.CountRecordsAsync("Booking", bookingId));
        string alert = Assert.Single(_store.Log.Errors, line => line.Contains(brokenEventId));
        Assert.Contains("AUDIT_EVENT_PARKED", alert);
        Assert.Contains("invalidEventData", alert);
    }
}
