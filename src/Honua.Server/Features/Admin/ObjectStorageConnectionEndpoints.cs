// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

// Routes that exist only for object storage connections. Create, update, delete, get, list and test
// share the database connection routes through ObjectStorageConnectionDispatch.
internal static class ObjectStorageConnectionEndpoints
{
    internal const string StoreUnavailableMessage = "Object storage connections need the PostgreSQL configuration store.";

    internal const string ConnectorUnavailableMessage = "Object storage is not available on this host.";

    internal const string TooManySetupsMessage =
        "Too many object storage setups are open. Finish or abandon one; unused setups expire after a day.";

    private const string QuickCreateBase = "https://console.aws.amazon.com/cloudformation/home#/stacks/quickcreate";

    public static void MapObjectStorageConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/connections")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Secure Connections")
            .RequireAdminAuthorization();

        // Each call mints and stores a new external id bound to the caller, so it is a POST.
        group.MapPost("/object-storage/setup", HandleSetupAsync)
            .WithName("CreateObjectStorageConnectionSetup")
            .WithDisplayName("Create Object Storage Connection Setup")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}/objects", HandleBrowseAsync)
            .WithName("ListConnectionObjects")
            .WithDisplayName("Browse Connection Objects")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    // Mints a single-use external id for one bucket and prefix, bound to the caller. The id is returned once and never logged.
    private static async Task<IResult> HandleSetupAsync(
        [FromQuery] string? bucket,
        [FromQuery] string? prefix,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var normalized = ObjectStoragePolicy.NormalizeConnectionPrefix(prefix);
        if (!ObjectStoragePolicy.IsValidBucketName(bucket) || normalized is null)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("A valid S3 bucket name and a plain relative prefix of at most 256 characters are required."));
        }

        var services = context.RequestServices;
        if (services.GetService<IObjectStorageConnectionStore>() is not { } store)
        {
            return ServiceUnavailable(StoreUnavailableMessage);
        }

        var options = services.GetRequiredService<IOptions<ObjectStorageConnectionOptions>>().Value;
        var setup = new ObjectStorageConnectionSetup
        {
            ExternalId = ObjectStoragePolicy.NewExternalId(),
            MintedBy = SetupPrincipal(context),
            Bucket = bucket!,
            Prefix = normalized,
            ExpiresAt = services.GetRequiredService<TimeProvider>().GetUtcNow() + options.SetupLifetime,
        };
        if (!await store.SaveSetupAsync(setup, options.MaxOpenSetupsPerPrincipal, context.RequestAborted).ConfigureAwait(false))
        {
            return TypedResults.Json(ApiResponse<object>.Failure(TooManySetupsMessage), statusCode: StatusCodes.Status429TooManyRequests);
        }

        await RecordSetupAsync(context, setup).ConfigureAwait(false);

        return TypedResults.Ok(ApiResponse<ObjectStorageSetupResponse>.CreateSuccess(new ObjectStorageSetupResponse
        {
            ExternalId = setup.ExternalId,
            QuickCreateUrl = QuickCreateUrl(options, setup),
            RoleNamePrefix = ObjectStorageConnectionKinds.RoleNamePrefix,
        }));
    }

    // The external id is never recorded.
    private static Task RecordSetupAsync(HttpContext context, ObjectStorageConnectionSetup setup)
        => context.RequestServices.GetRequiredService<SecureConnectionGovernance>().RecordAsync(
            context,
            "connection.setup",
            AuditOutcome.Success,
            resourceId: null,
            new SecureConnectionAuditDetails { Provider = ObjectStorageConnectionKinds.Provider, Bucket = setup.Bucket, Prefix = setup.Prefix },
            context.RequestAborted);

    private static async Task<IResult> HandleBrowseAsync(Guid id, [FromQuery] string? prefix, HttpContext context)
    {
        var services = context.RequestServices;
        var connection = await services.GetRequiredService<IObjectStorageConnectionResolver>()
            .FindAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (connection is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
        }

        var browsePrefix = ObjectStoragePolicy.ResolveBrowsePrefix(connection.Prefix, prefix);
        if (browsePrefix is null)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("The prefix must be a plain folder path under the connection prefix."));
        }

        if (services.GetService<IObjectStoreConnector>() is not { } connector)
        {
            return ServiceUnavailable(ConnectorUnavailableMessage);
        }

        try
        {
            var page = await connector.BrowseAsync(
                new ObjectStorageBrowseRequest { Connection = connection, Prefix = browsePrefix },
                context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ObjectStorageBrowseResponse>.CreateSuccess(ToResponse(page)));
        }
        catch (ImportObjectStoreException ex)
        {
            return ex.Code == ImportObjectStoreProblems.NotFound
                ? TypedResults.NotFound(ApiResponse<object>.Failure(ex.Message))
                : TypedResults.Json(ApiResponse<object>.Failure(ex.Message), statusCode: StatusCodes.Status403Forbidden);
        }
    }

    // A stable identity for binding a setup to its minter: shared admin keys all carry the name "admin",
    // so the key id comes first, then the signed-in subject.
    internal static string SetupPrincipal(HttpContext context)
    {
        var user = context.User;
        if (user.FindFirst("api_key_id")?.Value is { Length: > 0 } keyId)
        {
            return "api-key:" + keyId;
        }

        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(subject) ? "name:" + context.GetUserIdentity() : "subject:" + subject;
    }

    internal static string? QuickCreateUrl(ObjectStorageConnectionOptions options, ObjectStorageConnectionSetup setup)
    {
        if (!Uri.TryCreate(options.QuickCreateTemplateUrl, UriKind.Absolute, out var template) || template.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var slug = setup.Bucket.Replace('.', '-');
        var roleName = (ObjectStorageConnectionKinds.RoleNamePrefix + slug)[..Math.Min(64, ObjectStorageConnectionKinds.RoleNamePrefix.Length + slug.Length)];
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("templateURL", template.AbsoluteUri),
            new("stackName", roleName),
            new("param_RoleName", roleName),
            new("param_BucketName", setup.Bucket),
            new("param_KeyPrefix", setup.Prefix),
            new("param_ExternalId", setup.ExternalId),
        };
        if (!string.IsNullOrWhiteSpace(options.TrustedPrincipalArn))
        {
            parameters.Add(new("param_TrustedPrincipalArn", options.TrustedPrincipalArn));
        }

        return QuickCreateBase + "?" + string.Join('&', parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
    }

    internal static IResult ServiceUnavailable(string message)
        => TypedResults.Json(ApiResponse<object>.Failure(message), statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ObjectStorageBrowseResponse ToResponse(ObjectStorageBrowseResult page) => new()
    {
        Bucket = page.Bucket,
        Prefix = page.Prefix,
        Folders = page.Folders,
        Objects = page.Objects.Select(entry => new ObjectStorageObjectResponse
        {
            Key = entry.Key,
            Bytes = entry.Bytes,
            Kind = entry.Kind,
            LastModified = entry.LastModified,
        }).ToList(),
        Truncated = page.Truncated,
    };
}
