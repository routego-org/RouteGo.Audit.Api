# 10. Audit Service

| | |
|---|---|
| **Type** | Worker + read API |
| **Build phase** | 9 |
| **Port** | 5009 |
| **Data** | DynamoDB table `routego-audit-log` (no SQL) |
| **Instance** | `routego-secure` (different trust zone from the services it audits) |
| **Repo path** | `src/Audit` |

---

## 1. BRD: why it exists
- Sensitive actions (refunds, operator approvals, force-cancels) must be recorded so that finance and support disputes can be settled (FR-H4).
- If the audit trail sits in the same database as everything else, anyone with database access can edit history.

**Business outcome:** a record that cannot be changed after the fact, even by administrators of the other services.

## 2. PRD: what it offers
| Capability | Behavior |
|---|---|
| Record | Who did what to which entity, when, and why |
| Browse | Admin screen: by entity (for example one booking) or by day |

Audited actions: operator approved or rejected (Identity), booking cancelled by customer or force-cancelled by admin (Booking), refund issued (Payment).

Rules:
- Records are append-only: no update, no delete, enforced by IAM.
- The same event recorded twice results in one record.
- Only Admin can read.

## 3. FRD: how it behaves

### 3.1 Event consumed
`AuditRecorded` from `routego-audit-events` via `audit-q`:
```json
{ "actorId": "uuid", "actorRole": "Admin", "action": "BookingForceCancelled",
  "entityType": "Booking", "entityId": "uuid", "details": { "reason": "...", "refundPercent": 100 },
  "occurredAt": "2026-10-02T10:15:30Z" }
```
Publishers include an audit event in the same outbox transaction as the action, so an action cannot happen without its audit event eventually existing.

### 3.2 DynamoDB design
Table `routego-audit-log`, on-demand, point-in-time recovery on, deletion protection on.
| Attribute | Value |
|---|---|
| `pk` (partition) | `ENTITY#<entityType>#<entityId>` |
| `sk` (sort) | `<occurredAt ISO>#<eventId>` |
| `gsi1pk` | `DAY#<yyyy-mm-dd>` |
| `gsi1sk` | `<occurredAt ISO>#<eventId>` |
| attributes | actorId, actorRole, action, details (map), eventId, correlationId |

Write: `PutItem` with `ConditionExpression: attribute_not_exists(pk)` (a replay of the same event fails the condition, which the worker treats as success).
Reads: one entity's history = `Query` on `pk`; one day's activity = `Query` on `gsi1`.

### 3.3 Immutability by permission
IAM policy on `routego-secure-role` for this table: `PutItem`, `GetItem`, `Query` only. No `UpdateItem`, `DeleteItem`, `BatchWriteItem`. An explicit `Deny` on `UpdateItem` and `DeleteItem` for the table is added too. Table deletion protection stops accidental deletion. Students then try to edit a record and are denied.

### 3.4 Endpoints
| Method and path | Auth | Purpose |
|---|---|---|
| `GET /audit?entityType=&entityId=` | Admin | History of one entity |
| `GET /audit?date=yyyy-mm-dd&limit=&cursor=` | Admin | One day's records |
| `GET /health`, `/health/ready` | none | Probes |

### 3.5 Config
| Where | Key |
|---|---|
| SSM `/routego/prod/audit/` | `table-name`, `page-size` (50) |

## 4. Dependencies
**Depends on (sync):** none.
**Consumes:** `AuditRecorded` from Identity, Booking, Payment.

**Depended on by:** Web Portal (admin audit screen).

## 5. AWS services used and why
| AWS service | Use | Without it |
|---|---|---|
| **DynamoDB** | Append-only log with cheap key lookups | A SQL table that DB users can edit |
| **IAM (PutItem-only)** | Immutability by permission, not by developer discipline | "We promise not to edit" is not control |
| **Point-in-time recovery** | Restore after accidental damage | Lost history |
| **SQS (+ DLQ)** | Reliable delivery | An audit event lost during a deployment |
| **CloudTrail** | AWS-level audit: who changed a security group, IAM policy, or read a secret | You cannot answer infrastructure questions |
| **Separate EC2 trust zone** | Audit runs with a role the audited services lack | A compromised service could rewrite its own history |
| **KMS** | Table encryption | n/a |

**What students learn:** application audit vs infrastructure audit (this service vs CloudTrail), enforcing immutability through IAM, and DynamoDB single-table access by entity and by day.

## 6. Before building: AWS setup
1. DynamoDB table `routego-audit-log` (keys `pk`, `sk`; GSI `gsi1` on `gsi1pk`/`gsi1sk`), PITR on, deletion protection on, SSE-KMS.
2. Queue `audit-q` + DLQ; subscription to `routego-audit-events`.
3. Confirm the secure role's table policy (PutItem, GetItem, Query, plus the explicit deny).
4. SSM parameters; ECR repo `routego/audit`; log group.
5. CloudTrail trail active (Phase 0). Optionally send CloudTrail to CloudWatch Logs.
6. Alarm on `audit-dlq`.

## 7. Local development
1. LocalStack creates the table and queue.
2. `dotnet run --project src/Audit`.
3. Approve an operator in Identity locally; confirm a record appears: `awslocal dynamodb scan --table-name routego-audit-log`.
4. Replay the same event: still one record.
5. LocalStack does not enforce IAM by default, so the immutability demo is done only on AWS (the local environment demonstrates the data model).

## 8. Deploying to AWS
1. Prerequisites above.
2. Build and push `routego/audit`; SSM parameters.
3. Compose block on `routego-secure` (`5009:8080`, log group `/routego/audit`).
4. Security group: `sg-secure` allows 5009 from `sg-edge` (Portal).
5. Verify:
   - Force-cancel a booking as admin: record appears under the booking's `pk`.
   - Portal admin audit screen shows it.
6. **Break it on purpose:**
   - From the `routego-secure` instance with its role, run `aws dynamodb update-item` and `delete-item` on a record: **AccessDenied**. Compare with the same actions as an administrator, then discuss how admin access is itself controlled and logged.
   - Open CloudTrail event history and find the denied call (infrastructure audit).
   - Delete a record as admin and restore the table with point-in-time recovery to a new table.
   - Stop Audit while performing actions; restart; the records appear.

## 9. Lab checklist
- [ ] Update and delete denied for the service role.
- [ ] Duplicate event creates one record.
- [ ] Students can name what the audit service sees vs what CloudTrail sees.
