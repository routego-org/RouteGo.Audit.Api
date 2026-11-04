# CAP-AUD-001 — Record audit event

- Capability: CAP-AUD-001
- Canonical spec: `capabilities/audit/features/record-audit-event.md` in the Audit product-specs repo (`D:\Project-learn-aws\Insight\Audit`, feature spec 0.1.1, registry 0.1.2); not vendored as a submodule yet — that repo has no commit
- Area: AuditLog
- Slice: `Features/AuditLog/RecordAuditEvent/`

## Contract

`kind: worker` — no HTTP operation. Trigger `queue: audit-events`: the queue `AuditEventsQueueUrl` (`audit-q`),
subscribed to SNS `routego-audit-events` with raw delivery and filter `eventType` = `AuditRecorded`. It is read by
`Infrastructure/Messaging/AuditEventsService` (an `EventQueueService`), which hands each event to
`RecordAuditEventHandler.HandleAsync`.

- Envelope facts used: `eventId` (the event's identity), `occurredAt` (when the action happened), `correlationId`.
- Data (`AuditEventMessage`): `actorId`, `actorRole`, `action`, `entityType`, `entityId` (all required text, kept as given) and
  `details` (optional JSON object, kept as written).
- Applied (recorded, or already recorded) → message deleted. A missing actor, role, action, entity kind or identifier, or a
  `details` that is not an object → postponed with reason `invalidEventData`: the message stays and is delivered again; on the
  5th delivery `AUDIT_EVENT_PARKED` is logged naming the event, then the queue's redrive policy parks it on `audit-dlq`.
  A failure of the store is retried the same way (reason `repeatedFailure`). Events behind a bad one are not blocked.

## Acceptance criteria

Each criterion maps to tests in `RecordAuditEvent.Tests.cs`; the queue paths are in
`Infrastructure/Messaging/AuditEventsService.Tests.cs`.

- [x] AC-1: an audited action becomes a record with who, what, to which, when and why — `HandleAsync_AdminForceCancelsBooking_WritesOneRecordWithWhoWhatWhichWhenAndWhy`
- [x] AC-2: the same event delivered five times leaves one record — `HandleAsync_SameEventDeliveredFiveTimes_LeavesOneRecord`
- [x] AC-3: a redelivered event never alters the record — `HandleAsync_RedeliveryWithDifferentReasonAndTime_KeepsTheFirstRecord`
- [x] AC-4: two actions on one entity leave two records — `HandleAsync_TwoActionsOnOneBooking_LeavesTwoRecords`
- [x] AC-5: a late event is placed by when the action happened — `HandleAsync_LateEventArrivesLast_IsPlacedBeforeTheLaterAction`
- [x] AC-6: Audit catches up after being stopped — `AuditEventsServiceTests.HandleWaitingEventsAsync_TwentyEventsWaitedWhileStopped_RecordsEachOnce`
- [x] AC-7: an action name Audit has not seen before is still recorded — `HandleAsync_ActionNameNeverSeenBefore_IsRecordedAsGiven`
- [x] AC-8: an event missing who, what or to which is parked and the rest continue — `HandleAsync_EventNamesNoEntity_IsPostponedAsInvalidEventDataAndWritesNothing`;
  `AuditEventsServiceTests.HandleWaitingEventsAsync_EventMissingItsEntity_IsRetriedThenParkedWithAnAlertNamingItWhileTheOthersAreRecorded`
- [x] AC-9: an automatic refund is recorded with the system actor — `HandleAsync_AutomaticRefundBySystemActor_IsRecordedWithTheSystemRole`
- Business day (browse AC-3 depends on it) — `HandleAsync_ActionJustAfterMidnightIndiaTime_IsFiledUnderTheNewBusinessDay`

## Invariants touched

- One record per event, keyed by the event's identity alone: a marker item `pk = EVENT#<eventId>`, `sk = EVENT` and the record are
  written in one `TransactWriteItems`, each with `attribute_not_exists(pk)`. A refused condition means the event was recorded
  before, and counts as applied. (10-audit.md keys on entity + time + event id, which would record a second copy of a redelivery
  whose time differs; the feature spec's AC-3 and the data model forbid that.)
- Record layout: `pk = ENTITY#<entityType>#<entityId>`, `sk = <occurredAt, UTC fixed width>#<eventId>`, `gsi1pk = DAY#<yyyy-MM-dd India Standard Time>`,
  `gsi1sk` = `sk`; spelled once in `Domain/AuditKeys.cs`. The marker carries no index keys, so it never shows in a history or a day.
- The service's role needs `PutItem`, `GetItem` and `Query` only (a transaction of puts needs only `PutItem`); nothing here updates or deletes.
