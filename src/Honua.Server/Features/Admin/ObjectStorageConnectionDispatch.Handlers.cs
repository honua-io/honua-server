// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ObjectStorage;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static partial class ObjectStorageConnectionDispatch
{
    private const string RoleArnMessage = "roleArn must be an IAM role named honua-connection-*.";

    private static async Task<IResult> CreateObjectStorageAsync(CreateSecureConnectionRequest request, HttpContext http)
    {
        var (draft, error) = Validate(request);
        if (draft is null)
        {
            return Invalid(error!);
        }

        if (http.RequestServices.GetService<IObjectStorageConnectionStore>() is not { } store)
        {
            return ObjectStorageConnectionEndpoints.ServiceUnavailable(ObjectStorageConnectionEndpoints.StoreUnavailableMessage);
        }

        var connection = draft with
        {
            Id = Guid.NewGuid(),
            CreatedBy = http.GetUserIdentity(),
        };
        var outcome = await store
            .CreateAsync(connection, ObjectStorageConnectionEndpoints.SetupPrincipal(http), http.RequestAborted)
            .ConfigureAwait(false);
        if (outcome != ObjectStorageCreateOutcome.Created)
        {
            return Invalid(SetupRejectedMessage);
        }

        await RecordAsync(http, "connection.create", connection).ConfigureAwait(false);
        return TypedResults.Created(
            $"/api/v1/admin/connections/{connection.Id:D}",
            ApiResponse<ObjectStorageConnectionCreatedResponse>.CreateSuccess(new ObjectStorageConnectionCreatedResponse
            {
                Id = connection.Id,
                Name = connection.Name,
            }));
    }

    // A draft test may only use an external id this server minted and that is still open.
    private static async Task<IResult> TestDraftObjectStorageAsync(CreateSecureConnectionRequest request, HttpContext http)
    {
        var (draft, error) = Validate(request);
        if (draft is null)
        {
            return Invalid(error!);
        }

        if (http.RequestServices.GetService<IObjectStorageConnectionStore>() is not { } store)
        {
            return ObjectStorageConnectionEndpoints.ServiceUnavailable(ObjectStorageConnectionEndpoints.StoreUnavailableMessage);
        }

        var setup = await store.FindOpenSetupAsync(draft.ExternalId!, http.RequestAborted).ConfigureAwait(false);
        if (setup is null
            || setup.MintedBy != ObjectStorageConnectionEndpoints.SetupPrincipal(http)
            || setup.Bucket != draft.Bucket
            || setup.Prefix != draft.Prefix)
        {
            return Invalid(SetupRejectedMessage);
        }

        return await TestObjectStorageAsync(draft with { Id = Guid.Empty }, http).ConfigureAwait(false);
    }

    private static async Task<IResult> TestObjectStorageAsync(ObjectStorageConnection connection, HttpContext http)
    {
        if (http.RequestServices.GetService<IObjectStoreConnector>() is not { } connector)
        {
            return ObjectStorageConnectionEndpoints.ServiceUnavailable(ObjectStorageConnectionEndpoints.ConnectorUnavailableMessage);
        }

        var result = await connector.TestAsync(connection, http.RequestAborted).ConfigureAwait(false);
        await RecordTestAsync(http, connection, result).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<ConnectionTestResult>.CreateSuccess(new ConnectionTestResult
        {
            ConnectionId = connection.Id,
            ConnectionName = connection.Name,
            IsHealthy = result.IsHealthy,
            TestedAt = DateTimeOffset.UtcNow,
            Message = result.Message,
        }));
    }

    private static async Task<IResult> UpdateObjectStorageAsync(
        ObjectStorageConnection connection,
        UpdateSecureConnectionRequest? request,
        HttpContext http)
    {
        if (connection.BuiltIn)
        {
            return ReadOnly();
        }

        var (updated, error) = ApplyUpdate(connection, request);
        if (updated is null)
        {
            return Invalid(error!);
        }

        var store = http.RequestServices.GetRequiredService<IObjectStorageConnectionStore>();
        if (!await store.UpdateAsync(updated, http.RequestAborted).ConfigureAwait(false))
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
        }

        await RecordUpdateAsync(http, updated, connection).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<SecureConnectionSummary>.CreateSuccess(ToSummary(updated)));
    }

    private static async Task<IResult> DeleteObjectStorageAsync(ObjectStorageConnection connection, HttpContext http)
    {
        if (connection.BuiltIn)
        {
            return ReadOnly();
        }

        var store = http.RequestServices.GetRequiredService<IObjectStorageConnectionStore>();
        if (!await store.DeleteAsync(connection.Id, http.RequestAborted).ConfigureAwait(false))
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
        }

        await RecordAsync(http, "connection.delete", connection).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Connection deleted"));
    }

    internal static (ObjectStorageConnection? Draft, string? Error) Validate(CreateSecureConnectionRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        (bool Failed, string? Message)[] checks =
        [
            (name.Length is < 1 or > 64, "name must be 1 to 64 characters."),
            (request.Description is { Length: > 1000 }, "description must be at most 1000 characters."),
            (!string.Equals(request.Provider, ObjectStorageConnectionKinds.Provider, StringComparison.Ordinal), "provider must be aws-s3."),
            (!ObjectStoragePolicy.IsValidBucketName(request.Bucket), "bucket is not a valid S3 bucket name."),
            (ObjectStoragePolicy.NormalizeConnectionPrefix(request.Prefix) is null, "prefix must be a plain relative path of at most 256 characters."),
            (!ObjectStoragePolicy.IsKnownRegion(request.Region), "region is not a known AWS region."),
            (!ObjectStoragePolicy.IsValidRoleArn(request.RoleArn), RoleArnMessage),
            (string.IsNullOrWhiteSpace(request.ExternalId), "externalId is required; request one from the object storage setup endpoint."),
        ];
        var error = Array.Find(checks, static check => check.Failed).Message;
        if (error is not null)
        {
            return (null, error);
        }

        return (new ObjectStorageConnection
        {
            Id = Guid.Empty,
            Name = name,
            Description = request.Description,
            Bucket = request.Bucket!,
            Prefix = ObjectStoragePolicy.NormalizeConnectionPrefix(request.Prefix)!,
            Region = request.Region!,
            RoleArn = request.RoleArn,
            ExternalId = request.ExternalId!.Trim(),
        }, null);
    }

    internal static (ObjectStorageConnection? Updated, string? Error) ApplyUpdate(
        ObjectStorageConnection connection,
        UpdateSecureConnectionRequest? request)
    {
        if (request is null)
        {
            return (null, "A request body is required.");
        }

        var prefixChanged = request.Prefix is not null
            && ObjectStoragePolicy.NormalizePrefix(request.Prefix) != connection.Prefix;
        (bool Failed, string? Message)[] checks =
        [
            (request.ExternalId is not null, "externalId cannot be changed."),
            ((request.Bucket is not null && request.Bucket != connection.Bucket) || prefixChanged, "bucket and prefix cannot be changed; create a new connection instead."),
            (request.Name is not null && request.Name.Trim().Length is < 1 or > 64, "name must be 1 to 64 characters."),
            (request.Description is { Length: > 1000 }, "description must be at most 1000 characters."),
            (request.Region is not null && !ObjectStoragePolicy.IsKnownRegion(request.Region), "region is not a known AWS region."),
            (request.RoleArn is not null && !ObjectStoragePolicy.IsValidRoleArn(request.RoleArn), RoleArnMessage),
        ];
        var error = Array.Find(checks, static check => check.Failed).Message;
        if (error is not null)
        {
            return (null, error);
        }

        return (connection with
        {
            Name = request.Name?.Trim() ?? connection.Name,
            Description = request.Description ?? connection.Description,
            Region = request.Region ?? connection.Region,
            RoleArn = request.RoleArn ?? connection.RoleArn,
        }, null);
    }
}
