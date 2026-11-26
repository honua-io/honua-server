// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Exports;

internal sealed record ExportCatalogReadOptions(
    string BaseUrl,
    Func<ExportVisibilitySubject, CancellationToken, ValueTask<bool>>? IsVisible = null);

internal sealed record ExportCatalogListRequest(ExportCatalogReadOptions Read, ExportListPage Page);

internal sealed record ExportCatalogLookupRequest(string ExportId, ExportCatalogReadOptions Read);

// Enforces caller-scoped export visibility across listing pagination, detail lookups, and
// cross-tenant export-id collision checks without expanding ExportCatalog's storage duties.
internal static class ExportCatalogVisibility
{
    private readonly record struct VisibleStateQuery(
        string ExportId,
        ExportCatalog.CatalogSnapshot Snapshot,
        Func<ExportVisibilitySubject, CancellationToken, ValueTask<bool>> IsVisible);

    public static async Task<IReadOnlyList<ExportResponse>> ListAsync(
        ExportCatalog catalog,
        ExportCatalogListRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Read.IsVisible is null)
        {
            return await catalog.ListAsync(request.Read.BaseUrl, request.Page, cancellationToken)
                .ConfigureAwait(false);
        }

        var pageLimit = Math.Clamp(request.Page.Limit ?? ExportCatalog.DefaultListLimit, 1, ExportCatalog.MaxListLimit);
        var pageOffset = Math.Max(0, request.Page.Offset ?? 0);
        var snapshot = await catalog.CaptureSnapshotAsync(subFolder: null, cancellationToken).ConfigureAwait(false);
        var (ids, _) = ExportCatalog.IndexExports(snapshot);
        var items = new List<ExportResponse>();
        foreach (var id in ids)
        {
            var query = new VisibleStateQuery(id, snapshot, request.Read.IsVisible);
            var state = await LoadVisibleStateAsync(catalog, query, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                continue;
            }

            if (pageOffset > 0)
            {
                pageOffset--;
                continue;
            }

            items.Add(ExportCatalog.DescribeSummary(state, request.Read.BaseUrl));
            if (items.Count == pageLimit)
            {
                break;
            }
        }

        return items;
    }

    public static async Task<ExportResponse?> GetAsync(
        ExportCatalog catalog,
        ExportCatalogLookupRequest lookup,
        CancellationToken cancellationToken)
    {
        if (lookup.Read.IsVisible is null)
        {
            return await catalog.GetAsync(lookup.ExportId, lookup.Read.BaseUrl, cancellationToken)
                .ConfigureAwait(false);
        }

        var snapshot = await catalog.CaptureSnapshotAsync(lookup.ExportId, cancellationToken).ConfigureAwait(false);
        var query = new VisibleStateQuery(lookup.ExportId, snapshot, lookup.Read.IsVisible);
        var state = await LoadVisibleStateAsync(catalog, query, cancellationToken).ConfigureAwait(false);
        return state is null
            ? null
            : await catalog.DescribeDetailAsync(state, lookup.Read.BaseUrl, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> ExistsForAnotherTenantAsync(
        ExportCatalog catalog,
        ExportCatalogLookupRequest lookup,
        CancellationToken cancellationToken)
    {
        var isVisible = lookup.Read.IsVisible;
        if (isVisible is null)
        {
            return false;
        }

        var snapshot = await catalog.CaptureSnapshotAsync(lookup.ExportId, cancellationToken).ConfigureAwait(false);
        var state = await catalog.LoadStateAsync(lookup.ExportId, snapshot, cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            var subject = await BuildVisibilitySubjectAsync(catalog, state, cancellationToken).ConfigureAwait(false);
            return !await isVisible(subject, cancellationToken).ConfigureAwait(false);
        }

        var unpublished = await catalog.TryReadUnpublishedMarkerAsync(lookup.ExportId, cancellationToken)
            .ConfigureAwait(false);
        if (unpublished is null)
        {
            return false;
        }

        var markerSubject = new ExportVisibilitySubject
        {
            ExportId = lookup.ExportId,
            ServiceId = unpublished.ServiceId,
            ResourceId = unpublished.LayerId
        };
        return !await isVisible(markerSubject, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExportCatalog.ExportState?> LoadVisibleStateAsync(
        ExportCatalog catalog,
        VisibleStateQuery query,
        CancellationToken cancellationToken)
    {
        var state = await catalog.LoadStateAsync(query.ExportId, query.Snapshot, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var subject = await BuildVisibilitySubjectAsync(catalog, state, cancellationToken).ConfigureAwait(false);
        return await query.IsVisible(subject, cancellationToken).ConfigureAwait(false) ? state : null;
    }

    private static async Task<ExportVisibilitySubject> BuildVisibilitySubjectAsync(
        ExportCatalog catalog,
        ExportCatalog.ExportState state,
        CancellationToken cancellationToken)
    {
        if (state.Current is not null)
        {
            return new ExportVisibilitySubject
            {
                ExportId = state.ExportId,
                ServiceId = state.Current.SourceLayer.Service,
                ResourceId = state.Current.SourceLayer.Layer,
                LayerId = state.Job?.LayerId,
                JobId = state.Job?.JobId
            };
        }

        var (serviceId, resourceId) = state.VersionIds.Count > 0
            ? await catalog.ResolveSourceIdentityAsync(state.Store, state.ExportId, cancellationToken).ConfigureAwait(false)
            : (string.Empty, string.Empty);
        return new ExportVisibilitySubject
        {
            ExportId = state.ExportId,
            ServiceId = string.IsNullOrWhiteSpace(serviceId) ? state.Job?.ServiceId : serviceId,
            ResourceId = string.IsNullOrWhiteSpace(resourceId) ? null : resourceId,
            LayerId = state.Job?.LayerId,
            JobId = state.Job?.JobId
        };
    }
}
