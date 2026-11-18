// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ObjectStorage;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.Admin;

// Endpoint filters on the shared connection routes. A request whose kind is "object-storage", or whose
// id names an object storage connection, is answered here; every other request reaches the database
// connection handler unchanged.
internal static partial class ObjectStorageConnectionDispatch
{
    internal const string BuiltInReadOnlyMessage = "Built-in connections are read-only.";

    internal const string SetupRejectedMessage =
        "The external id is unknown, expired or already used. Request a new one from the object storage setup endpoint.";

    public static bool IsObjectStorage(string? kind)
        => string.Equals(kind?.Trim(), ObjectStorageConnectionKinds.Kind, StringComparison.OrdinalIgnoreCase);

    public static async ValueTask<object?> CreateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<CreateSecureConnectionRequest>().FirstOrDefault();
        return request is not null && IsObjectStorage(request.Kind)
            ? await CreateObjectStorageAsync(request, context.HttpContext).ConfigureAwait(false)
            : await next(context).ConfigureAwait(false);
    }

    public static async ValueTask<object?> TestDraftAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<CreateSecureConnectionRequest>().FirstOrDefault();
        return request is not null && IsObjectStorage(request.Kind)
            ? await TestDraftObjectStorageAsync(request, context.HttpContext).ConfigureAwait(false)
            : await next(context).ConfigureAwait(false);
    }

    public static async ValueTask<object?> ListAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context).ConfigureAwait(false);
        if (result is not INestedHttpResult { Result: Ok<ApiResponse<IReadOnlyList<SecureConnectionSummary>>> { Value.Data: { } rows } })
        {
            return result;
        }

        var objectStorage = await ListObjectStorageAsync(context.HttpContext).ConfigureAwait(false);
        if (objectStorage is null)
        {
            return result;
        }

        IReadOnlyList<SecureConnectionSummary> merged = [.. rows, .. objectStorage.Select(ToSummary)];
        return TypedResults.Ok(ApiResponse<IReadOnlyList<SecureConnectionSummary>>.CreateSuccess(merged));
    }

    public static ValueTask<object?> GetAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => ForObjectStorageAsync(context, next, static (connection, _) =>
            Task.FromResult<IResult>(TypedResults.Ok(ApiResponse<SecureConnectionSummary>.CreateSuccess(ToSummary(connection)))));

    public static ValueTask<object?> UpdateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => ForObjectStorageAsync(context, next, (connection, http) =>
            UpdateObjectStorageAsync(connection, context.Arguments.OfType<UpdateSecureConnectionRequest>().FirstOrDefault(), http));

    public static ValueTask<object?> DeleteAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => ForObjectStorageAsync(context, next, DeleteObjectStorageAsync);

    public static ValueTask<object?> TestAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => ForObjectStorageAsync(context, next, TestObjectStorageAsync);

    internal static SecureConnectionSummary ToSummary(ObjectStorageConnection connection) => new()
    {
        ConnectionId = connection.Id,
        Name = connection.Name,
        Description = connection.Description,
        Host = string.Empty,
        DatabaseName = string.Empty,
        Username = string.Empty,
        Provider = connection.Provider,
        SslMode = string.Empty,
        StorageType = string.Empty,
        IsActive = true,
        HealthStatus = "unknown",
        CreatedAt = connection.CreatedAt,
        CreatedBy = connection.CreatedBy,
        Kind = ObjectStorageConnectionKinds.Kind,
        Bucket = connection.Bucket,
        Prefix = connection.Prefix,
        Region = connection.Region,
        RoleArn = connection.RoleArn,
        BuiltIn = connection.BuiltIn,
    };

    private static async ValueTask<object?> ForObjectStorageAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        Func<ObjectStorageConnection, HttpContext, Task<IResult>> handle)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id))
        {
            return await next(context).ConfigureAwait(false);
        }

        ObjectStorageConnection? connection;
        try
        {
            connection = await http.RequestServices.GetRequiredService<IObjectStorageConnectionResolver>()
                .FindAsync(id, http.RequestAborted).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // The database connection routes keep working when the object storage table cannot be read.
            ObjectStorageReadFailed(Logger(http), ex);
            connection = null;
        }

        return connection is null
            ? await next(context).ConfigureAwait(false)
            : await handle(connection, http).ConfigureAwait(false);
    }

    // Null when the object storage store cannot be read; the database rows are then listed alone.
    private static async Task<IReadOnlyList<ObjectStorageConnection>?> ListObjectStorageAsync(HttpContext http)
    {
        try
        {
            return await http.RequestServices.GetRequiredService<IObjectStorageConnectionResolver>()
                .ListAsync(http.RequestAborted).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            ObjectStorageReadFailed(Logger(http), ex);
            return null;
        }
    }

    private static ILogger Logger(HttpContext http)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ObjectStorageConnectionDispatch));

    [LoggerMessage(EventId = 4590, Level = LogLevel.Warning, Message = "Object storage connections could not be read; database connections are served alone.")]
    private static partial void ObjectStorageReadFailed(ILogger logger, Exception exception);

    private static Task RecordAsync(HttpContext http, string action, ObjectStorageConnection connection)
        => RecordAsync(http, new AuditEntry
        {
            Action = action,
            Id = connection.Id,
            Details = Details(connection),
        });

    private static Task RecordUpdateAsync(HttpContext http, ObjectStorageConnection updated, ObjectStorageConnection previous)
        => RecordAsync(http, new AuditEntry
        {
            Action = "connection.update",
            Id = updated.Id,
            Details = Details(updated) with
            {
                PreviousRegion = previous.Region,
                PreviousRoleArn = previous.RoleArn,
            },
        });

    // A draft test has no connection id yet.
    private static Task RecordTestAsync(HttpContext http, ObjectStorageConnection connection, ObjectStorageTestResult result)
        => RecordAsync(http, new AuditEntry
        {
            Action = "connection.test",
            Outcome = result.IsHealthy ? AuditOutcome.Success : AuditOutcome.Failure,
            Id = connection.Id,
            Details = Details(connection) with { Healthy = result.IsHealthy },
        });

    private static Task RecordAsync(HttpContext http, AuditEntry entry)
        => http.RequestServices.GetRequiredService<SecureConnectionGovernance>().RecordAsync(
            http,
            entry.Action,
            entry.Outcome,
            entry.Id == Guid.Empty ? null : entry.Id.ToString(),
            entry.Details,
            http.RequestAborted);

    // What the customer role grants; the external id is never recorded.
    private static SecureConnectionAuditDetails Details(ObjectStorageConnection connection) => new()
    {
        Name = connection.Name,
        Provider = connection.Provider,
        Bucket = connection.Bucket,
        Prefix = connection.Prefix,
        Region = connection.Region,
        RoleArn = connection.RoleArn,
    };

    private static BadRequest<ApiResponse<object>> Invalid(string message) => TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {message}"));

    private static Conflict<ApiResponse<object>> ReadOnly() => TypedResults.Conflict(ApiResponse<object>.Failure(BuiltInReadOnlyMessage));

    private sealed record AuditEntry
    {
        public required string Action { get; init; }

        public AuditOutcome Outcome { get; init; } = AuditOutcome.Success;

        public required Guid Id { get; init; }

        public required SecureConnectionAuditDetails Details { get; init; }
    }
}
