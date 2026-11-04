# RouteGo.Audit.Api

The permanent, append-only audit log of RouteGo. It records every `AuditRecorded` event that Identity, Booking, Seat Inventory and Payment publish, and lets an admin read the log by entity or by business day.

- **Specs:** `D:\Project-learn-aws\Insight\Audit` (capabilities `CAP-AUD-001` record, `CAP-AUD-002` browse; contract `contracts/audit/openapi.yaml`). System context: `00-overall-architecture.md`, service sheet: `10-audit.md`.
- **Runs on:** `routego-secure`, port 5009 (container) — a trust zone the audited services do not share.
- **Data:** DynamoDB table `routego-audit-log` (`pk`/`sk`, index `gsi1`). No SQL.

## Slices

| Slice | What | Capability |
|---|---|---|
| `Features/AuditLog/RecordAuditEvent` | worker on `audit-q`: one record per event, keyed by event id | CAP-AUD-001 |
| `Features/AuditLog/SearchAuditRecords` | `GET /audit` (Admin): entity history or business day, `page`/`pageSize` | CAP-AUD-002 |

`/health` is liveness; `/health/ready` reads one key of the audit table.

## Run locally

```bash
docker compose up -d                    # LocalStack (audit topic, audit-q, audit-dlq) + DynamoDB Local
bash local/dynamodb/create-table.sh     # the audit table
dotnet run --project RouteGo.Audit.Api  # http://localhost:7628 (the run profile's name-derived port)
dotnet test                             # real DynamoDB Local + LocalStack through Testcontainers; needs Docker
```

Configuration is flat environment variables named in `Infrastructure/ConfigKeys.cs`; the local values are in `Infrastructure/LocalEntryPoints.cs`. There is no `appsettings*.json` and no SSM read in the app — the deployment injects the same names (`deploy/ec2/docker-compose.yml`).

## Where this follows the specs repo over `10-audit.md`

| Topic | `10-audit.md` | Built (spec / contract) |
|---|---|---|
| Paging | `limit`, `cursor`, default 50 | `page`, `pageSize` (default 20, max 200), `totalCount` |
| Business day | UTC `DAY#yyyy-mm-dd` | India Standard Time day |
| Dedupe | `attribute_not_exists(pk)` on entity + time + event id | event id alone: a marker item and the record in one transaction |
| Routes | two query shapes | one operation, `searchAuditRecords` |
| Bad event | — | retried, then parked (`AUDIT_EVENT_PARKED` log line, then `audit-dlq`) |

## Immutability

`deploy/ec2/audit-table-policy.json` gives the instance role `PutItem`, `GetItem`, `Query` and an explicit `Deny` on `UpdateItem`, `DeleteItem`, `BatchWriteItem`. Local DynamoDB does not enforce IAM, so the denial can only be demonstrated on AWS.
