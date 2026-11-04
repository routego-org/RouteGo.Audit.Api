// Slice files carry no using directives — this file holds the shared set.
// The sibling slice skill and database setup EXTEND this list beneath the '// FILLED BY' marker below.
global using System.Text.Json;          // JsonSerializer, WireJson.Options
global using Microsoft.AspNetCore.Mvc;
global using RouteGo.Audit.Api.Common;             // HttpClients, WireJson
global using RouteGo.Audit.Api.Common.Ports;       // IClock
global using RouteGo.Audit.Api.Common.Problems;    // ProblemResult, ProblemTypes (catalog projection)
global using RouteGo.Audit.Api.Common.Behaviors;   // UnhandledExceptionHandler, ResponseMetadataMiddleware, ResponseHeadersTransformer
// FILLED BY the sibling slice skill — and by skills-db-postgres (copy-assets.ps1) at database setup:
global using Amazon.DynamoDBv2;                    // the audit log, queried and written by slice handlers
global using Amazon.DynamoDBv2.Model;
global using Amazon.SQS;                           // the audit queue
global using Amazon.SQS.Model;
global using FluentValidation;                     // slice validators
global using FluentValidation.Results;
global using Microsoft.AspNetCore.Authorization;   // [Authorize]
global using RouteGo.Audit.Api.Common.Messaging;   // ReceivedEvent, EventOutcome — the event-queue worker
global using RouteGo.Audit.Api.Domain;             // AuditKeys
