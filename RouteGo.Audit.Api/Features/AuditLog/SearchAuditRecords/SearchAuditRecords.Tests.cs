namespace RouteGo.Audit.Api.Features.AuditLog.SearchAuditRecords;

public sealed class SearchAuditRecordsTests : IClassFixture<SearchAuditRecordsAuditStore>
{
    private static readonly DateTimeOffset _nineOnSecondOctober = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly SearchAuditRecordsAuditStore _store;

    public SearchAuditRecordsTests(SearchAuditRecordsAuditStore store)
    {
        _store = store;
    }

    [Fact]
    public async Task SearchAuditRecords_EntityWithTwoRecords_ReturnsThemOldestFirstWithATotalOfTwo()
    {
        // Arrange
        string bookingId = NewId("BKG");
        await _store.PutRecordAsync("Booking", bookingId, "EVT-late", _nineOnSecondOctober.AddMinutes(5), null);
        await _store.PutRecordAsync("Booking", bookingId, "EVT-early", _nineOnSecondOctober, null);

        // Act
        SearchAuditRecordsResponse result = await _store.SearchAsync($"?entityType=Booking&entityId={bookingId}");

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new string[] { "EVT-early", "EVT-late" }, result.Items.Select(record => record.EventId));
        AuditRecordItem first = result.Items[0];
        Assert.Equal("meera-shah", first.ActorId);
        Assert.Equal("Admin", first.ActorRole);
        Assert.Equal("BookingForceCancelled", first.Action);
        Assert.Equal("Booking", first.EntityType);
        Assert.Equal(bookingId, first.EntityId);
        Assert.Equal(_nineOnSecondOctober, first.OccurredAt);
        Assert.Equal("corr-EVT-early", first.CorrelationId);
    }

    [Fact]
    public async Task SearchAuditRecords_BusinessDay_ReturnsThatDaysRecordsOldestFirstAndNoOtherDays()
    {
        // Arrange
        var quietDay = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.FromHours(5.5));
        string bookingId = NewId("BKG");
        string operatorId = NewId("ORB");
        await _store.PutRecordAsync("Operator", operatorId, "EVT-operator", quietDay.AddHours(2), null);
        await _store.PutRecordAsync("Booking", bookingId, "EVT-booking", quietDay, null);
        await _store.PutRecordAsync("Booking", NewId("BKG"), "EVT-next-day", quietDay.AddDays(1), null);

        // Act
        SearchAuditRecordsResponse result = await _store.SearchAsync("?date=2026-11-14");

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new string[] { "EVT-booking", "EVT-operator" }, result.Items.Select(record => record.EventId));
    }

    [Fact]
    public async Task SearchAuditRecords_ActionJustAfterMidnightIndiaTime_IsInTheNewDayAndNotTheOldOne()
    {
        // Arrange
        var halfPastMidnightIst = new DateTimeOffset(2026, 12, 2, 0, 30, 0, TimeSpan.FromHours(5.5));
        await _store.PutRecordAsync("Booking", NewId("BKG"), "EVT-midnight", halfPastMidnightIst, null);

        // Act
        SearchAuditRecordsResponse newDay = await _store.SearchAsync("?date=2026-12-02");
        SearchAuditRecordsResponse oldDay = await _store.SearchAsync("?date=2026-12-01");

        // Assert
        Assert.Equal(new string[] { "EVT-midnight" }, newDay.Items.Select(record => record.EventId));
        Assert.Empty(oldDay.Items);
    }

    [Fact]
    public async Task SearchAuditRecords_EntityWithNoHistoryAndDayWithNoActivity_AreSuccessfulEmptyLists()
    {
        // Arrange
        string unknownBooking = NewId("BKG");

        // Act
        SearchAuditRecordsResponse entity = await _store.SearchAsync($"?entityType=Booking&entityId={unknownBooking}");
        SearchAuditRecordsResponse day = await _store.SearchAsync("?date=2001-01-01");

        // Assert
        Assert.Empty(entity.Items);
        Assert.Equal(0, entity.TotalCount);
        Assert.Empty(day.Items);
        Assert.Equal(0, day.TotalCount);
    }

    [Fact]
    public async Task SearchAuditRecords_SecondPageOfOneHundredTwentyAtFiftyAPage_ReturnsRecordsFiftyOneToOneHundredWithTheTotal()
    {
        // Arrange
        string bookingId = NewId("BKG");
        await _store.PutRecordsAsync("Booking", bookingId, 120, _nineOnSecondOctober);

        // Act
        SearchAuditRecordsResponse result = await _store.SearchAsync($"?entityType=Booking&entityId={bookingId}&page=2&pageSize=50");

        // Assert
        Assert.Equal(120, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.Equal(50, result.Items.Count);
        Assert.Equal($"EVT-{bookingId}-050", result.Items[0].EventId);
        Assert.Equal($"EVT-{bookingId}-099", result.Items[49].EventId);
    }

    [Fact]
    public async Task SearchAuditRecords_PageBeyondTheEnd_ReturnsNoRecordsButTheTotal()
    {
        // Arrange
        string bookingId = NewId("BKG");
        await _store.PutRecordsAsync("Booking", bookingId, 3, _nineOnSecondOctober);

        // Act
        SearchAuditRecordsResponse result = await _store.SearchAsync($"?entityType=Booking&entityId={bookingId}&page=5&pageSize=50");

        // Assert
        Assert.Empty(result.Items);
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task SearchAuditRecords_NoPagingGiven_UsesPageOneOfTwenty()
    {
        // Arrange
        string bookingId = NewId("BKG");
        await _store.PutRecordsAsync("Booking", bookingId, 25, _nineOnSecondOctober);

        // Act
        SearchAuditRecordsResponse result = await _store.SearchAsync($"?entityType=Booking&entityId={bookingId}");

        // Assert
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(20, result.Items.Count);
        Assert.Equal(25, result.TotalCount);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Customer")]
    public async Task SearchAuditRecords_NotAnAdmin_IsRefusedAsForbidden(string role)
    {
        // Arrange
        string query = "?entityType=Booking&entityId=BKG-501";

        // Act
        SearchAuditRecordsProblem problem = await _store.GetProblemAsync(query, SearchAuditRecordsAuditStore.TokenFor(role));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("FORBIDDEN", problem.Code);
    }

    [Fact]
    public async Task SearchAuditRecords_NoTokenOrAnUntrustedOne_IsRefusedAsUnauthorized()
    {
        // Arrange
        string query = "?entityType=Booking&entityId=BKG-501";

        // Act
        SearchAuditRecordsProblem withoutToken = await _store.GetProblemAsync(query, null);
        SearchAuditRecordsProblem untrusted = await _store.GetProblemAsync(query, SearchAuditRecordsAuditStore.UntrustedAdminToken());

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.Status);
        Assert.Equal("UNAUTHORIZED", withoutToken.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, untrusted.Status);
        Assert.Equal("UNAUTHORIZED", untrusted.Code);
    }

    [Fact]
    public async Task SearchAuditRecords_NeitherEntityNorDay_IsRefusedAsValidationFailed()
    {
        // Arrange
        string query = string.Empty;

        // Act
        SearchAuditRecordsProblem problem = await _store.GetProblemAsync(query, SearchAuditRecordsAuditStore.TokenFor("Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains("entityId", problem.ErrorFields);
        Assert.Contains("date", problem.ErrorFields);
    }

    [Fact]
    public async Task SearchAuditRecords_BothEntityAndDay_IsRefusedAsValidationFailed()
    {
        // Arrange
        string query = "?entityType=Booking&entityId=BKG-501&date=2026-10-02";

        // Act
        SearchAuditRecordsProblem problem = await _store.GetProblemAsync(query, SearchAuditRecordsAuditStore.TokenFor("Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains("date", problem.ErrorFields);
    }

    [Fact]
    public async Task SearchAuditRecords_EntityKindWithoutIdentifier_IsRefusedNamingTheMissingIdentifier()
    {
        // Arrange
        string query = "?entityType=Booking";

        // Act
        SearchAuditRecordsProblem problem = await _store.GetProblemAsync(query, SearchAuditRecordsAuditStore.TokenFor("Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Equal(new string[] { "entityId" }, problem.ErrorFields);
    }

    [Theory]
    [InlineData("?date=2nd-October", "date")]
    [InlineData("?date=2026-02-30", "date")]
    [InlineData("?date=2026-10-02&page=0", "page")]
    [InlineData("?date=2026-10-02&pageSize=0", "pageSize")]
    [InlineData("?date=2026-10-02&pageSize=201", "pageSize")]
    public async Task SearchAuditRecords_DayOrPagingOutOfShape_IsRefusedNamingTheField(string query, string field)
    {
        // Arrange
        string accessToken = SearchAuditRecordsAuditStore.TokenFor("Admin");

        // Act
        SearchAuditRecordsProblem problem = await _store.GetProblemAsync(query, accessToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Contains(field, problem.ErrorFields);
    }

    [Fact]
    public async Task SearchAuditRecords_RecordWithEveryKindOfDetail_ShowsItExactlyAsWritten()
    {
        // Arrange
        string bookingId = NewId("BKG");
        var nested = new Dictionary<string, AttributeValue> { { "refunded", new AttributeValue { BOOL = true } } };
        var details = new Dictionary<string, AttributeValue>
        {
            { "reason", new AttributeValue { S = "Documents verified" } },
            { "refundPercent", new AttributeValue { N = "100" } },
            { "amount", new AttributeValue { N = "2160.50" } },
            { "notified", new AttributeValue { BOOL = false } },
            { "note", new AttributeValue { NULL = true } },
            { "seats", new AttributeValue { L = new List<AttributeValue> { new AttributeValue { S = "1A" }, new AttributeValue { S = "1B" } }, IsLSet = true } },
            { "outcome", new AttributeValue { M = nested, IsMSet = true } },
        };
        await _store.PutRecordAsync("Booking", bookingId, "EVT-9001", _nineOnSecondOctober.AddYears(-1), details);

        // Act
        SearchAuditRecordsResponse result = await _store.SearchAsync($"?entityType=Booking&entityId={bookingId}");

        // Assert
        Dictionary<string, JsonElement> shown = Assert.Single(result.Items).Details!;
        Assert.Equal("Documents verified", shown["reason"].GetString());
        Assert.Equal(100, shown["refundPercent"].GetInt32());
        Assert.Equal(2160.50m, shown["amount"].GetDecimal());
        Assert.False(shown["notified"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, shown["note"].ValueKind);
        Assert.Equal("1B", shown["seats"][1].GetString());
        Assert.True(shown["outcome"].GetProperty("refunded").GetBoolean());
    }

    private static string NewId(string prefix)
    {
        return prefix + "-" + Guid.NewGuid();
    }
}
