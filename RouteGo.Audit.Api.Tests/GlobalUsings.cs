// Slice test files (linked in from {Project}/Features/**/*.Tests.cs) carry no using directives —
// this file holds the test project's shared set. The sibling slice skill EXTENDS it as its
// test helpers need more (e.g. Testcontainers, AngleSharp).
global using Xunit;
global using RouteGo.Audit.Api.Common;             // HttpClients, WireJson
global using System.Net;                       // HttpStatusCode
global using System.Net.Http.Headers;          // bearer Authorization header
global using System.Net.Http.Json;             // ReadFromJsonAsync
global using System.Security.Cryptography;     // RSA test signing keys
global using System.Text.Json;                 // JsonDocument for raw problem bodies
global using Amazon.DynamoDBv2;                // the audit log, seeded and read directly by slice tests
global using Amazon.DynamoDBv2.Model;
global using Amazon.SQS;                       // the audit queue, in LocalStack
global using Amazon.SQS.Model;
global using Microsoft.AspNetCore.Authentication.JwtBearer;
global using Microsoft.Extensions.Configuration;   // in-memory settings for the worker's handler
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Logging.Abstractions;
global using Microsoft.IdentityModel.JsonWebTokens;          // minting test access tokens
global using Microsoft.IdentityModel.Protocols;
global using Microsoft.IdentityModel.Protocols.OpenIdConnect;
global using Microsoft.IdentityModel.Tokens;
global using RouteGo.Audit.Api.Common.Messaging;   // ReceivedEvent, EventOutcome — the event-queue worker
global using RouteGo.Audit.Api.Domain;             // AuditKeys
global using RouteGo.Audit.Api.Tests;              // ApiFactory
global using Testcontainers.DynamoDb;          // DynamoDB Local in Docker
global using Testcontainers.LocalStack;        // LocalStack in Docker
global using RouteGo.Audit.Api.Common.Ports;       // IClock
