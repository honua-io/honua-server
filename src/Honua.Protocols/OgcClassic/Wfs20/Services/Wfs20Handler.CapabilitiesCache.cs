// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed partial class Wfs20Handler
{
    public async Task<IResult> HandleGetCapabilitiesResultAsync(
        HttpContext context,
        IReadOnlySet<string>? requestedSections,
        string baseUrl)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_capabilities", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetCapabilities);
        Wfs20Log.GetCapabilitiesRequested(_logger);

        try
        {
            var result = await ResolveCachedCapabilitiesResultAsync(context, requestedSections, baseUrl)
                .ConfigureAwait(false);
            Wfs20Log.GetCapabilitiesReturned(_logger);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    private async Task<IResult> ResolveCachedCapabilitiesResultAsync(
        HttpContext context,
        IReadOnlySet<string>? requestedSections,
        string baseUrl)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var featureTypes = SelectAdvertisedFeatureTypes(
            await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false));
        var snapshot = await _metadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = BuildWfsCapabilitiesCacheKey(
            new WfsCapabilitiesCacheRequest
            {
                SnapshotEtag = snapshot.Etag,
                BaseUrl = baseUrl,
                SectionsMask = ComputeWfsSectionsMask(requestedSections)
            },
            featureTypes);
        return await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(context, cacheKey, async () =>
        {
            var model = await BuildCapabilitiesModelAsync(context, new WfsCapabilitiesBuildInput
            {
                Snapshot = snapshot,
                RequestedSections = requestedSections,
                BaseUrl = baseUrl,
                FeatureTypes = featureTypes
            }, cancellationToken).ConfigureAwait(false);
            return Wfs20DispatcherEndpoint.BuildCapabilitiesResult(model);
        }).ConfigureAwait(false);
    }

    private static int ComputeWfsSectionsMask(IReadOnlySet<string>? requestedSections)
    {
        var mask = 0;
        if (ShouldIncludeCapabilitiesSection(requestedSections, "ServiceIdentification")) mask |= 1 << 0;
        if (ShouldIncludeCapabilitiesSection(requestedSections, "ServiceProvider")) mask |= 1 << 1;
        if (ShouldIncludeCapabilitiesSection(requestedSections, "OperationsMetadata")) mask |= 1 << 2;
        if (ShouldIncludeCapabilitiesSection(requestedSections, "FeatureTypeList")) mask |= 1 << 3;
        if (ShouldIncludeCapabilitiesSection(requestedSections, "Filter_Capabilities")) mask |= 1 << 4;
        return mask;
    }

    private static string BuildWfsCapabilitiesCacheKey(
        WfsCapabilitiesCacheRequest request,
        ImmutableArray<WfsFeatureTypeDescriptor> featureTypes)
    {
        var sb = PooledTextBuilder.Rent();
        try
        {
            sb.Append("wfs|").Append(request.SnapshotEtag).Append('|').Append(request.BaseUrl)
                .Append('|').Append(request.SectionsMask);
            foreach (var ft in featureTypes)
            {
                sb.Append('|').Append(ft.StorageLayerId)
                    .Append(':').Append(ft.QualifiedName)
                    .Append(':').Append(ft.NamespaceUri);
            }

            return MetadataDocumentByteCache.HashKey(sb);
        }
        finally
        {
            PooledTextBuilder.Return(sb);
        }
    }
}
