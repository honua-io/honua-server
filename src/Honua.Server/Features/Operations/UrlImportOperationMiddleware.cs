// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Import.FileImport;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Operations;

// The operation executor's authenticated loopback omits Idempotency-Key, so it reaches
// the existing downloader once. No caller-supplied lineage header can skip this adapter.
internal sealed class UrlImportOperationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            context.GetEndpoint()?.Metadata.GetMetadata<UrlImportOperationMetadata>() is null ||
            !context.Request.Headers.ContainsKey("Idempotency-Key"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null || !await ImportAdminAuthorization.IsAuthorizedAsync(context, context.User, cancellationToken).ConfigureAwait(false))
        {
            await ProblemAsync(context, StatusCodes.Status403Forbidden, "Import mutation is not authorized.").ConfigureAwait(false);
            return;
        }
        if (!OperationsEndpoints.TryResolveIdempotencyKey(context, out var key, out var error) || key is null)
        {
            await ProblemAsync(context, StatusCodes.Status400BadRequest, error ?? "Idempotency-Key header must not be empty.").ConfigureAwait(false);
            return;
        }

        ImportUrlImportRequest? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync(context.Request.Body, ImportJsonContext.Default.ImportUrlImportRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await ProblemAsync(context, StatusCodes.Status400BadRequest, "A valid URL import request is required.").ConfigureAwait(false);
            return;
        }
        if (input is null || string.IsNullOrWhiteSpace(input.SourceUrl) || string.IsNullOrWhiteSpace(input.TableName))
        {
            await ProblemAsync(context, StatusCodes.Status400BadRequest, "SourceUrl and TableName are required.").ConfigureAwait(false);
            return;
        }

        // The source-generated DTO fixes field order and fills defaults before hashing.
        // Only its digest is persisted; signed source URLs never become evidence references.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(input, ImportJsonContext.Default.ImportUrlImportRequest);
        using var document = JsonDocument.Parse(canonical);
        var parameters = document.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText(), StringComparer.Ordinal);
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        var invoker = context.RequestServices.GetRequiredService<IOperationInvoker>();
        var handle = await invoker.SubmitAsync(new OperationRequest
        {
            OperationId = "admin.import.upload-url",
            Parameters = parameters,
        }, new OperationPolicyContext
        {
            PrincipalId = actor,
            TenantId = tenant,
            SchemaName = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
            IdempotencyKey = ScopeKey(tenant, actor, key!),
            IdempotencyRequestHash = Convert.ToHexStringLower(SHA256.HashData(canonical)),
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            AuthorizationOutcome = "authorized",
            Tier = context.RequestServices.GetService<ILicenseEntitlementService>()?.GetSnapshot().Edition.ToString().ToLowerInvariant(),
            Roles = context.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
            ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(context.User),
            RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(context.User).Order(StringComparer.Ordinal).ToArray(),
        }, cancellationToken).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "no-store";
        var details = handle.Result?.Details;
        if (details is not null && details.TryGetValue("statusCode", out var statusText) &&
            int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 200 and <= 599 &&
            details.TryGetValue("response", out var response) &&
            (status >= 400 || (!string.IsNullOrWhiteSpace(handle.AuditId) &&
                handle.Status is OperationHandleStatus.Queued or OperationHandleStatus.Running or OperationHandleStatus.Completed)))
        {
            context.Response.Headers["X-Honua-Operation-Instance-Id"] = handle.OperationInstanceId;
            context.Response.Headers["X-Honua-Audit-Id"] = handle.AuditId;
            context.Response.Headers["X-Correlation-ID"] = handle.CorrelationId;
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A conflict or refusal never returns the existing job, audit identity, or payload.
        var refusedStatus = details?.GetValueOrDefault("statusCode") == "409" ? StatusCodes.Status409Conflict
            : handle.Status == OperationHandleStatus.Denied ? StatusCodes.Status403Forbidden
            : handle.Status == OperationHandleStatus.RequiresApproval ? StatusCodes.Status202Accepted
            : StatusCodes.Status503ServiceUnavailable;
        await ProblemAsync(context, refusedStatus, handle.Reason ?? "The URL import could not be durably dispatched.").ConfigureAwait(false);
    }

    private static string ScopeKey(string? tenant, string actor, string key)
    {
        // Length-prefix components: delimiter characters in authenticated identities cannot
        // cause two distinct tenant/principal/key tuples to share an invocation.
        var identity = FormattableString.Invariant($"{tenant?.Length ?? -1}:{tenant}{actor.Length}:{actor}{key.Length}:{key}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static Task ProblemAsync(HttpContext context, int status, string detail) =>
        ProblemDetailsHelpers.CreateAdminProblem(context, status, ProblemDetailsHelpers.GetTitle(status), detail).ExecuteAsync(context);
}
