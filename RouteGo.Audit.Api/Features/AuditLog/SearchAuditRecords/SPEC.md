# CAP-AUD-002 — Search audit records

- Capability: CAP-AUD-002
- Canonical spec: `capabilities/audit/features/browse-audit-log.md` in the Audit product-specs repo (`D:\Project-learn-aws\Insight\Audit`, feature spec 0.1.0, registry 0.1.2); not vendored as a submodule yet — that repo has no commit
- Area: AuditLog
- Slice: `Features/AuditLog/SearchAuditRecords/`

## Contract
Implemented **verbatim** from the issued contract — if it looks wrong, report drift upstream
(`skills-spec-authoring`), never deviate locally.

- Operation: `searchAuditRecords` in `contracts/audit/openapi.yaml` → `contracts/audit/audit-record.yaml`
- Route / verb: `GET audit`
- Request: query `entityType` + `entityId` (both) **or** `date` (yyyy-MM-dd), with `page` (default 1) and `pageSize` (default 20, 1 to 200)
- Response: `200` `AuditRecordListResponse` (`items`, `totalCount`, `page`, `pageSize`), oldest first
- Problem codes: `VALIDATION_FAILED` (400), `FORBIDDEN` (403); `UNAUTHORIZED` (401) comes from the bearer handler

## Acceptance criteria
Each criterion maps to tests in `SearchAuditRecords.Tests.cs`.

- [x] AC-1: one entity's history, oldest first — `SearchAuditRecords_EntityWithTwoRecords_ReturnsThemOldestFirstWithATotalOfTwo`
- [x] AC-2: one day's records — `SearchAuditRecords_BusinessDay_ReturnsThatDaysRecordsOldestFirstAndNoOtherDays`
- [x] AC-3: just after midnight India time belongs to the new day — `SearchAuditRecords_ActionJustAfterMidnightIndiaTime_IsInTheNewDayAndNotTheOldOne`
- [x] AC-4: no history or no activity is an empty list — `SearchAuditRecords_EntityWithNoHistoryAndDayWithNoActivity_AreSuccessfulEmptyLists`
- [x] AC-5: records come back in pages — `SearchAuditRecords_SecondPageOfOneHundredTwentyAtFiftyAPage_ReturnsRecordsFiftyOneToOneHundredWithTheTotal`,
  `SearchAuditRecords_PageBeyondTheEnd_ReturnsNoRecordsButTheTotal`, `SearchAuditRecords_NoPagingGiven_UsesPageOneOfTwenty`
- [x] AC-6: anyone but an admin is refused — `SearchAuditRecords_NotAnAdmin_IsRefusedAsForbidden`, `SearchAuditRecords_NoTokenOrAnUntrustedOne_IsRefusedAsUnauthorized`
- [x] AC-7: neither an entity nor a day — `SearchAuditRecords_NeitherEntityNorDay_IsRefusedAsValidationFailed`
- [x] AC-8: both an entity and a day — `SearchAuditRecords_BothEntityAndDay_IsRefusedAsValidationFailed`
- [x] AC-9: an entity named in full — `SearchAuditRecords_EntityKindWithoutIdentifier_IsRefusedNamingTheMissingIdentifier`
- [x] AC-10: a record exactly as written — `SearchAuditRecords_RecordWithEveryKindOfDetail_ShowsItExactlyAsWritten`
- Paging and day shape — `SearchAuditRecords_DayOrPagingOutOfShape_IsRefusedNamingTheField`

## Invariants touched
Business rules live in `Domain/`, never in this slice.

- Key layout and the India Standard Time business day: `Domain/AuditKeys.cs` (shared with the recording worker, CAP-AUD-001).
- Day text and paging bounds: `Domain/AuditSearchRules.cs`.
- Read only: no operation here adds, changes or removes a record; the service's role holds `GetItem`, `Query` and `PutItem` on the table only.
- Authorization is the role claim: Admin only, enforced before the handler runs.

## Notes
- The total is a `Select=COUNT` query over the same partition as the page, run alongside it; a page is reached by skipping keys only, so a very large day costs reads proportional to its size (spec open question 5 asks whether that is affordable).
- A request giving an entity and a day is refused with the error on `date`; the contract names no field for it.
