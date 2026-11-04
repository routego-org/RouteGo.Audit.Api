namespace RouteGo.Audit.Api.Features.AuditLog.SearchAuditRecords;

/// <summary>
/// What an admin asks the audit log: either one entity's history (kind and identifier, both) or
/// one business day, never both and never neither.
/// </summary>
public sealed record SearchAuditRecordsRequest
{
    /// <summary>The kind of entity whose history is wanted, for example Booking; given with <see cref="EntityId"/>.</summary>
    public string? EntityType { get; init; }

    /// <summary>The identifier of the entity whose history is wanted; given with <see cref="EntityType"/>.</summary>
    public string? EntityId { get; init; }

    /// <summary>The business day wanted, written yyyy-MM-dd, instead of an entity.</summary>
    public string? Date { get; init; }

    /// <summary>The page wanted, counted from 1.</summary>
    public int Page { get; init; } = AuditSearchRules.DefaultPage;

    /// <summary>How many records a page holds, from 1 to 200.</summary>
    public int PageSize { get; init; } = AuditSearchRules.DefaultPageSize;
}

/// <summary>The permanent note of one audited action, exactly as it was written.</summary>
public sealed record AuditRecordItem
{
    /// <summary>The identity of the audit event the record was written from.</summary>
    public required string EventId { get; init; }

    /// <summary>Who performed the action.</summary>
    public required string ActorId { get; init; }

    /// <summary>The role the actor acted in, for example Admin.</summary>
    public required string ActorRole { get; init; }

    /// <summary>What was done, for example BookingForceCancelled.</summary>
    public required string Action { get; init; }

    /// <summary>The kind of thing the action was done to, for example Booking.</summary>
    public required string EntityType { get; init; }

    /// <summary>The identifier of the thing the action was done to.</summary>
    public required string EntityId { get; init; }

    /// <summary>When the action happened (UTC).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The reason and figures that explain the action, as the publisher gave them; absent when none were given.</summary>
    public Dictionary<string, JsonElement>? Details { get; init; }

    /// <summary>Ties the action to the request that caused it.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>One page of matching records, oldest first, with how many match in all.</summary>
public sealed record SearchAuditRecordsResponse
{
    /// <summary>The records on this page, oldest first.</summary>
    public required IReadOnlyList<AuditRecordItem> Items { get; init; }

    /// <summary>How many records match across all pages.</summary>
    public required int TotalCount { get; init; }

    /// <summary>The page returned, counted from 1.</summary>
    public required int Page { get; init; }

    /// <summary>The page size the answer was cut with.</summary>
    public required int PageSize { get; init; }
}

/// <summary>
/// Checks that the request names one entity in full or one day, and that the paging is one the log
/// can serve, before the log is asked.
/// </summary>
public sealed class SearchAuditRecordsValidator : AbstractValidator<SearchAuditRecordsRequest>
{
    private const string NameATarget = "Name an entity (kind and identifier) or a day.";
    private const string ChooseOne = "Choose either an entity or a day, not both.";

    public SearchAuditRecordsValidator()
    {
        RuleFor(x => x).Custom(CheckTarget);
        RuleFor(x => x.Page).Must(AuditSearchRules.IsValidPage).WithMessage("page must be 1 or more.");
        RuleFor(x => x.PageSize).Must(AuditSearchRules.IsValidPageSize)
            .WithMessage($"pageSize must be from 1 to {AuditSearchRules.MaxPageSize}.");
    }

    private static void CheckTarget(SearchAuditRecordsRequest request, ValidationContext<SearchAuditRecordsRequest> context)
    {
        bool hasEntity = !string.IsNullOrWhiteSpace(request.EntityType) || !string.IsNullOrWhiteSpace(request.EntityId);
        bool hasDay = !string.IsNullOrWhiteSpace(request.Date);

        if (!hasEntity && !hasDay)
        {
            context.AddFailure("entityId", NameATarget);
            context.AddFailure("date", NameATarget);
            return;
        }

        if (hasEntity && hasDay)
        {
            context.AddFailure("date", ChooseOne);
            return;
        }

        if (hasDay)
        {
            CheckDay(request, context);
            return;
        }

        CheckEntity(request, context);
    }

    private static void CheckDay(SearchAuditRecordsRequest request, ValidationContext<SearchAuditRecordsRequest> context)
    {
        if (!AuditSearchRules.IsBusinessDay(request.Date))
        {
            context.AddFailure("date", "The day must be a calendar date written yyyy-MM-dd.");
        }
    }

    private static void CheckEntity(SearchAuditRecordsRequest request, ValidationContext<SearchAuditRecordsRequest> context)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType))
        {
            context.AddFailure("entityType", "Name the kind of entity.");
        }

        if (string.IsNullOrWhiteSpace(request.EntityId))
        {
            context.AddFailure("entityId", "Name the entity's identifier.");
        }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "AuditLog")]
[Route("audit")]
public sealed class SearchAuditRecordsController : ControllerBase
{
    private readonly SearchAuditRecordsHandler _handler;
    private readonly SearchAuditRecordsValidator _validator = new();

    public SearchAuditRecordsController(IAmazonDynamoDB dynamoDb, IConfiguration configuration)
    {
        // Present by the time any request arrives: startup refuses to run without it.
        string tableName = configuration[Infrastructure.ConfigKeys.AuditTableName]!;
        _handler = new SearchAuditRecordsHandler(dynamoDb, tableName);
    }

    /// <summary>List the audit records of one entity or of one business day, oldest first (Admin only).</summary>
    /// <param name="request">The entity (kind and identifier) or the day, and the page wanted.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The matching records; no match is an empty list with a total of 0.</response>
    /// <response code="400">The request names neither an entity nor a day, names both, or names an entity without its identifier (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an admin (FORBIDDEN).</response>
    [Authorize(Roles = AccessRoles.Admin)]
    [HttpGet]
    [ProducesResponseType(typeof(SearchAuditRecordsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SearchAuditRecords([FromQuery] SearchAuditRecordsRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        SearchAuditRecordsResponse response = await _handler.HandleAsync(request, cancellationToken);
        return Ok(response);
    }
}
