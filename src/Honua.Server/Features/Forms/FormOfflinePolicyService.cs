// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Forms;

internal sealed class FormOfflinePolicyService
{
    private const string ClientIdHeader = "X-Honua-Client-Id";
    private readonly IFormPackageStore _store;
    private readonly ILogger<FormOfflinePolicyService> _logger;

    public FormOfflinePolicyService(IFormPackageStore store, ILogger<FormOfflinePolicyService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> GetAsync(HttpContext context, string formId)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.OfflinePolicy");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "offline-policy");
        activity?.SetTag("honua.forms.form_id", formId);

        var packageVersion = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, context.RequestAborted)
            .ConfigureAwait(false);
        if (packageVersion is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' was not found.");
        }

        var response = BuildResponse(context, packageVersion);
        FormOfflinePolicyLog.PolicyServed(_logger, formId, packageVersion.Version, response.AvailableTransports.Length);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Json(response, FormPackageJsonContext.Default.FormOfflinePolicyResponse);
    }

    private static FormOfflinePolicyResponse BuildResponse(HttpContext context, FormPackageVersion packageVersion)
    {
        var package = packageVersion.Package;
        var policy = package.OfflinePolicy;
        var serviceId = package.Target?.ServiceId ?? string.Empty;
        var layerId = package.Target?.LayerId ?? 0;
        var links = new List<FormLink>();
        var transports = new List<string>();

        if (policy.Enabled && policy.ReplicaTransportEnabled)
        {
            transports.Add("feature-server-replica");
            AddReplicaLinks(context, serviceId, links);
        }

        if (policy.Enabled && policy.FieldCollectionTransportEnabled)
        {
            transports.Add("fieldcollection");
            AddFieldCollectionLinks(context, links);
        }

        return new FormOfflinePolicyResponse
        {
            FormId = packageVersion.FormId,
            FormVersion = packageVersion.Version,
            Enabled = policy.Enabled,
            ServiceId = serviceId,
            LayerId = layerId,
            AvailableTransports = transports.ToArray(),
            Links = links.ToArray(),
            RequiredHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientIdHeader] = "Stable client or device identifier for cursor/idempotency coordination."
            }
        };
    }

    private static void AddReplicaLinks(HttpContext context, string serviceId, List<FormLink> links)
    {
        var encodedService = Uri.EscapeDataString(serviceId);
        links.Add(new FormLink { Rel = "create-replica", Href = Absolute(context, $"/rest/services/{encodedService}/FeatureServer/createReplica"), Method = HttpMethods.Post });
        links.Add(new FormLink { Rel = "extract-changes", Href = Absolute(context, $"/rest/services/{encodedService}/FeatureServer/extractChanges"), Method = HttpMethods.Post });
        links.Add(new FormLink { Rel = "synchronize-replica", Href = Absolute(context, $"/rest/services/{encodedService}/FeatureServer/synchronizeReplica"), Method = HttpMethods.Post });
        links.Add(new FormLink { Rel = "unregister-replica", Href = Absolute(context, $"/rest/services/{encodedService}/FeatureServer/unRegisterReplica"), Method = HttpMethods.Post });
    }

    private static void AddFieldCollectionLinks(HttpContext context, List<FormLink> links)
    {
        links.Add(new FormLink { Rel = "fieldcollection-generation", Href = Absolute(context, "/api/v1/fieldcollection/generation"), Method = HttpMethods.Get });
        links.Add(new FormLink { Rel = "fieldcollection-sync-cursor", Href = Absolute(context, "/api/v1/fieldcollection/sync-cursor"), Method = HttpMethods.Get });
        links.Add(new FormLink { Rel = "fieldcollection-ack-cursor", Href = Absolute(context, "/api/v1/fieldcollection/sync-cursor"), Method = HttpMethods.Post });
        links.Add(new FormLink { Rel = "fieldcollection-changes", Href = Absolute(context, "/api/v1/fieldcollection/changes"), Method = HttpMethods.Get });
        links.Add(new FormLink { Rel = "fieldcollection-push-change", Href = Absolute(context, "/api/v1/fieldcollection/changes"), Method = HttpMethods.Post });
    }

    private static string Absolute(HttpContext context, string path)
        => $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{path}";
}

internal static partial class FormOfflinePolicyLog
{
    [LoggerMessage(EventId = 118420, Level = LogLevel.Information, Message = "Served offline policy for form package {FormId} version {Version} with {TransportCount} transport(s).")]
    public static partial void PolicyServed(ILogger logger, string formId, int version, int transportCount);
}
