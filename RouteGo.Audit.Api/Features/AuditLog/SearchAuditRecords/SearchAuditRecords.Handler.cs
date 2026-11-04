using System.Globalization;

namespace RouteGo.Audit.Api.Features.AuditLog.SearchAuditRecords;

/// <summary>
/// Reads one page of an entity's history, or of a business day, from the audit log. A history is one
/// partition and a day is one index partition, both already in the order the action happened, so a
/// page is a slice of one ordered query and the total is a count of the same query.
/// </summary>
public sealed partial class SearchAuditRecordsHandler
{
    private const string PartitionPlaceholder = ":pk";

    // Keys only, for skipping over the pages before the one asked for: the index's own key parts
    // are what DynamoDB needs to resume from, and the base table's are always among them.
    private const string KeysProjection = "pk, sk, gsi1pk, gsi1sk";

    // Skipping reads this many keys per round trip at most, so a very large page number costs
    // several modest reads rather than one request DynamoDB would refuse.
    private const int MaxSkipPerRead = 1000;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public SearchAuditRecordsHandler(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    public async Task<SearchAuditRecordsResponse> HandleAsync(SearchAuditRecordsRequest request, CancellationToken cancellationToken)
    {
        // The count and the page are independent reads, so they run together.
        Task<int> countTask = CountAsync(request, cancellationToken);
        Task<List<Dictionary<string, AttributeValue>>> pageTask = ReadPageAsync(request, cancellationToken);
        await Task.WhenAll(countTask, pageTask);

        int totalCount = await countTask;
        List<Dictionary<string, AttributeValue>> items = await pageTask;
        return ToResponse(request, totalCount, items);
    }

    private QueryRequest NewQuery(SearchAuditRecordsRequest request)
    {
        var query = new QueryRequest
        {
            TableName = _tableName,
            ScanIndexForward = true,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(),
        };

        DateOnly? day = AuditSearchRules.ParseBusinessDay(request.Date);
        if (day is null)
        {
            query.KeyConditionExpression = "pk = :pk";
            string partitionKey = AuditKeys.EntityPartitionKey(request.EntityType!.Trim(), request.EntityId!.Trim());
            query.ExpressionAttributeValues[PartitionPlaceholder] = new AttributeValue { S = partitionKey };
            return query;
        }

        query.IndexName = AuditKeys.DayIndexName;
        query.KeyConditionExpression = "gsi1pk = :pk";
        query.ExpressionAttributeValues[PartitionPlaceholder] = new AttributeValue { S = AuditKeys.BusinessDayKey(day.Value) };
        return query;
    }

    private async Task<int> CountAsync(SearchAuditRecordsRequest request, CancellationToken cancellationToken)
    {
        QueryRequest query = NewQuery(request);
        query.Select = Select.COUNT;

        int total = 0;
        do
        {
            QueryResponse page = await _dynamoDb.QueryAsync(query, cancellationToken);
            total += page.Count ?? 0;
            query.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (HasMore(query.ExclusiveStartKey));

        return total;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ReadPageAsync(
        SearchAuditRecordsRequest request, CancellationToken cancellationToken)
    {
        long skip = (long)(request.Page - 1) * request.PageSize;
        Dictionary<string, AttributeValue>? startKey = await FindStartKeyAsync(request, skip, cancellationToken);
        if (skip > 0 && startKey is null)
        {
            // The log ends before this page begins.
            return new List<Dictionary<string, AttributeValue>>();
        }

        return await ReadItemsAsync(request, startKey, cancellationToken);
    }

    // The key of the last record before the page, or null when the page is the first or the log ends first.
    private async Task<Dictionary<string, AttributeValue>?> FindStartKeyAsync(
        SearchAuditRecordsRequest request, long skip, CancellationToken cancellationToken)
    {
        QueryRequest query = NewQuery(request);
        query.ProjectionExpression = KeysProjection;

        long remaining = skip;
        Dictionary<string, AttributeValue>? lastKey = null;
        while (remaining > 0)
        {
            query.Limit = (int)Math.Min(remaining, MaxSkipPerRead);
            query.ExclusiveStartKey = lastKey;
            QueryResponse page = await _dynamoDb.QueryAsync(query, cancellationToken);
            remaining -= page.Items.Count;
            if (!HasMore(page.LastEvaluatedKey))
            {
                return null;
            }

            lastKey = page.LastEvaluatedKey;
        }

        return lastKey;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ReadItemsAsync(
        SearchAuditRecordsRequest request, Dictionary<string, AttributeValue>? startKey, CancellationToken cancellationToken)
    {
        QueryRequest query = NewQuery(request);
        query.ExclusiveStartKey = startKey;

        var items = new List<Dictionary<string, AttributeValue>>();
        while (items.Count < request.PageSize)
        {
            query.Limit = request.PageSize - items.Count;
            QueryResponse page = await _dynamoDb.QueryAsync(query, cancellationToken);
            items.AddRange(page.Items);
            query.ExclusiveStartKey = page.LastEvaluatedKey;
            if (!HasMore(page.LastEvaluatedKey))
            {
                break;
            }
        }

        return items;
    }

    private static bool HasMore(Dictionary<string, AttributeValue>? key)
    {
        return key != null && key.Count > 0;
    }

    private static SearchAuditRecordsResponse ToResponse(
        SearchAuditRecordsRequest request, int totalCount, List<Dictionary<string, AttributeValue>> items)
    {
        var records = new List<AuditRecordItem>();
        foreach (Dictionary<string, AttributeValue> item in items)
        {
            records.Add(ToRecord(item));
        }

        return new SearchAuditRecordsResponse
        {
            Items = records,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    private static AuditRecordItem ToRecord(Dictionary<string, AttributeValue> item)
    {
        var record = new AuditRecordItem
        {
            EventId = item["eventId"].S,
            ActorId = item["actorId"].S,
            ActorRole = item["actorRole"].S,
            Action = item["action"].S,
            EntityType = item["entityType"].S,
            EntityId = item["entityId"].S,
            OccurredAt = DateTimeOffset.Parse(item["occurredAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            Details = ToDetails(item),
            CorrelationId = ToOptionalText(item, "correlationId"),
        };

        return record;
    }

    private static string? ToOptionalText(Dictionary<string, AttributeValue> item, string name)
    {
        if (!item.TryGetValue(name, out AttributeValue? value))
        {
            return null;
        }

        return value.S;
    }
}
