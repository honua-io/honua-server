// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.ServiceDefaults;
using Honua.Protocols.OData.Services;

namespace Honua.Protocols.OData;

internal sealed partial class ODataStreamingQueryHandler
{
    private const int MaximumSnapshotRows = 10000;
    private const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan SnapshotClockSkew = TimeSpan.FromMinutes(5);

    private sealed record DurableDeltaContinuation(ODataQuerySnapshot Snapshot, int Offset, bool Poll);

    private static IResult DeltaRecovery(HttpContext context, string code, string message, int status)
    {
        Activity.Current?.SetStatus(ActivityStatusCode.Error, code);
        return ODataUtilityService.CreateODataError(context, code, message, status);
    }

    private static async Task<(DurableDeltaContinuation? Continuation, IResult? Error)> ReadDurableDeltaAsync(
        HttpContext context, string token, CancellationToken cancellationToken)
    {
        var parts = token.Split('.');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[1], "N", out var id)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || parts[3] is not ("p" or "t") || (parts[3] == "t" && offset != 0))
        {
            return (null, DeltaRecovery(context, "InvalidQueryOption", "Malformed delta continuation.", 400));
        }
        var store = context.RequestServices.GetService<IQuerySnapshotStore>();
        if (store is null)
        {
            return (null, DeltaRecovery(context, "DeltaStoreUnavailable", "Durable change tracking is unavailable.", 503));
        }
        var payload = await store.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return (null, DeltaRecovery(context, "DeltaTokenExpired", "Delta state is missing or expired; obtain a tracked baseline.", 410));
        }
        var snapshot = JsonSerializer.Deserialize(payload, ODataQuerySnapshotJsonContext.Default.ODataQuerySnapshot);
        if (snapshot is null || snapshot.Id != id || snapshot.CreatedAt > DateTimeOffset.UtcNow + SnapshotClockSkew
            || snapshot.CreatedAt + SnapshotRetention + SnapshotClockSkew <= DateTimeOffset.UtcNow || offset > snapshot.Changes.Length)
        {
            return (null, DeltaRecovery(context, "DeltaTokenExpired", "Delta state cannot be continued; obtain a tracked baseline.", 410));
        }
        return (new DurableDeltaContinuation(snapshot, offset, parts[3] == "t"), null);
    }

    private async Task<IResult> HandleDurableDeltaAsync(
        HttpContext context, IFeatureReader reader, MetadataV2Resource resource, int storageLayerId,
        string metadataEtag, ODataDeltaService.DeltaQueryState query, DurableDeltaContinuation? continuation,
        int pageSize, int initialOffset, string? bbox, CancellationToken cancellationToken)
    {
        var store = context.RequestServices.GetService<IQuerySnapshotStore>();
        if (store is null)
        {
            return DeltaRecovery(context, "DeltaStoreUnavailable", "Durable change tracking is unavailable.", 503);
        }
        // A tracked query must have a complete baseline. Refuse query shapes for
        // which this materialization cannot preserve the public projection.
        if (pageSize <= 0 || initialOffset != 0 || !string.IsNullOrWhiteSpace(query.Expand)
            || !string.IsNullOrWhiteSpace(bbox) || ODataUtilityService.IsParquetFormat(query.Format))
        {
            return DeltaRecovery(context, "InvalidQueryOption", "Tracked queries require a positive page size and do not support $skip, $expand, bbox or Parquet.", 400);
        }

        var binding = await ComputeDeltaBindingAsync(context, resource, metadataEtag, cancellationToken).ConfigureAwait(false);
        if (continuation is not null && !string.Equals(binding, continuation.Snapshot.Binding, StringComparison.Ordinal))
        {
            return DeltaRecovery(context, "DeltaScopeChanged", "The query authorization scope changed; obtain a tracked baseline.", 410);
        }
        ODataQuerySnapshot snapshot;
        var offset = continuation?.Offset ?? 0;
        if (continuation is { Poll: false })
        {
            snapshot = continuation.Snapshot;
            if (pageSize != snapshot.PageSize)
            {
                return DeltaRecovery(context, "DeltaQueryMismatch", "A page continuation cannot change its page size.", 400);
            }
        }
        else
        {
            var (featureQuery, error) = await _querySearchService.BuildFeatureQueryAsync(
                query.Filter, query.OrderBy, pageSize, 0, resource,
                query.Select, null, true, query.Compute, query.Format, null, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return DeltaRecovery(context, "InvalidQuery", error, 400);
            }
            // The canonical reader applies provider routing, permanent filters, RLS
            // and masking. A single database read establishes the full query image;
            // pages never rerun an offset query against changing rows.
            var result = await reader.QueryAsync(storageLayerId,
                featureQuery with { Limit = MaximumSnapshotRows + 1 }, cancellationToken).ConfigureAwait(false);
            if (result.HasMoreResults || result.Items.Length > MaximumSnapshotRows)
            {
                return DeltaRecovery(context, "DeltaQueryTooLarge", "The tracked result exceeds the bounded snapshot capacity; narrow the filter.", 413);
            }
            if (result.TotalCount != result.Items.Length)
            {
                return DeltaRecovery(context, "DeltaSnapshotIncomplete", "The provider did not return a complete query image; retry the tracked query.", 409);
            }
            if (!ODataComputeService.TryParse(query.Compute, out var compute, out var computeError))
            {
                return DeltaRecovery(context, "InvalidQueryOption", computeError!, 400);
            }
            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(_crsRegistry, resource.ReadSrid() ?? 4326, cancellationToken).ConfigureAwait(false);
            var selected = ODataUtilityService.ParseSelect(query.Select);
            var items = new List<JsonElement>(result.Items.Length);
            var bytes = 0L;
            foreach (var feature in result.Items)
            {
                using var buffer = new MemoryStream();
                await using (var writer = new Utf8JsonWriter(buffer))
                {
                    await WriteODataFeatureAsync(writer, feature, query.LayerId, resource.ReadSrid() ?? 4326,
                        axisOrder, selected, compute, _geometryService, cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                bytes += buffer.Length;
                if (bytes > MaximumSnapshotBytes)
                {
                    return DeltaRecovery(context, "DeltaQueryTooLarge", "The tracked result exceeds the bounded snapshot capacity; narrow the projection.", 413);
                }
                using var document = JsonDocument.Parse(buffer.ToArray());
                items.Add(document.RootElement.Clone());
            }
            // If an authorization policy changed while the database was being read,
            // do not publish a receipt made under mixed policy versions.
            if (!string.Equals(binding, await ComputeDeltaBindingAsync(context, resource, metadataEtag, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            {
                return DeltaRecovery(context, "DeltaScopeChanged", "The query authorization scope changed; retry the baseline.", 410);
            }
            var current = items.ToArray();
            var changes = continuation is null ? current : ComputeDeltaChanges(continuation.Snapshot.Items, current, query.LayerId);
            snapshot = new ODataQuerySnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, query, binding, pageSize, current, changes,
                IsDelta: continuation is not null);
            var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, ODataQuerySnapshotJsonContext.Default.ODataQuerySnapshot);
            await store.SaveAsync(snapshot.Id, payload, snapshot.CreatedAt + SnapshotRetention, cancellationToken).ConfigureAwait(false);
        }

        using var response = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(response))
        {
            writer.WriteStartObject();
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
            if (ODataUtilityService.ShouldIncludeContext(context.Request, query.Format))
            {
                writer.WriteString("@odata.context", $"{baseUrl}/odata/$metadata#Features" + (snapshot.IsDelta ? "/$delta" : ""));
            }
            if (query.Count == true) { writer.WriteNumber("@odata.count", snapshot.Changes.Length); }
            writer.WritePropertyName("value");
            writer.WriteStartArray();
            var end = Math.Min((long)offset + pageSize, snapshot.Changes.Length);
            for (var index = offset; index < end; index++) { snapshot.Changes[index].WriteTo(writer); }
            writer.WriteEndArray();
            var hasMore = end < snapshot.Changes.Length;
            var token = $"v2.{snapshot.Id:N}.{(hasMore ? end : 0).ToString(CultureInfo.InvariantCulture)}.{(hasMore ? "p" : "t")}";
            writer.WriteString(hasMore ? "@odata.nextLink" : "@odata.deltaLink", $"{baseUrl}{context.Request.Path}?$deltatoken={token}");
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        ODataUtilityService.SetODataHeaders(context);
        ODataUtilityService.ApplyTrackChangesPreference(context);
        HonuaTelemetry.SetSuccess(Activity.Current, (int)Math.Min(pageSize, snapshot.Changes.Length - offset));
        return Results.Bytes(response.ToArray(), ODataUtilityService.GetODataContentType(context.Request, query.Format));
    }

    internal static JsonElement[] ComputeDeltaChanges(JsonElement[] previous, JsonElement[] current, int layerId)
    {
        var before = previous.ToDictionary(item => item.GetProperty("ObjectId").GetInt64());
        var after = current.ToDictionary(item => item.GetProperty("ObjectId").GetInt64());
        var changes = new List<JsonElement>();
        foreach (var id in before.Keys.Union(after.Keys).Order())
        {
            if (!after.TryGetValue(id, out var item))
            {
                // "changed" covers both a filter exit and a physical deletion: the
                // resource no longer belongs to this query. Keep the public keys for
                // the SDK's @removed identity projection; never reveal other fields.
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("ObjectId", id);
                    writer.WriteNumber("LayerId", layerId);
                    writer.WritePropertyName("@removed");
                    writer.WriteStartObject(); writer.WriteString("reason", "changed"); writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                using var document = JsonDocument.Parse(buffer.ToArray());
                changes.Add(document.RootElement.Clone());
            }
            else if (!before.TryGetValue(id, out var old) || !JsonElement.DeepEquals(old, item))
            {
                changes.Add(item);
            }
        }
        return changes.ToArray();
    }

    private static async Task<string> ComputeDeltaBindingAsync(HttpContext context, MetadataV2Resource resource, string metadataEtag, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(context.Request.Path.ToString());
        writer.Write(metadataEtag);
        writer.Write(context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema ?? "");
        writer.Write(context.RequestServices.GetService<ITenantContext>()?.TenantId ?? "");
        writer.Write(CanonicalSecurityActor.Resolve(context.User)?.ActorId ?? "anonymous");
        writer.Write(JsonSerializer.Serialize(resource, MetadataV2JsonContext.Default.MetadataV2Resource));
        var rls = context.RequestServices.GetService<IRowLevelSecurityFilterSource>();
        var predicate = rls is null ? null : await rls.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
        writer.Write(predicate?.Sql ?? "");
        if (predicate is not null)
        {
            foreach (var value in predicate.Parameters) { WriteBindingValue(writer, value); }
        }
        var masks = context.RequestServices.GetService<IFieldMaskSource>();
        if (masks is not null)
        {
            foreach (var name in (await masks.ResolveAsync(resource, cancellationToken).ConfigureAwait(false)).Order(StringComparer.Ordinal))
            {
                writer.Write(name);
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static void WriteBindingValue(BinaryWriter writer, object? value)
    {
        writer.Write(value?.GetType().FullName ?? "null");
        if (value is Array values)
        {
            writer.Write(values.Length);
            foreach (var item in values) { WriteBindingValue(writer, item); }
        }
        else { writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); }
    }
}

internal sealed record ODataQuerySnapshot(Guid Id, DateTimeOffset CreatedAt, ODataDeltaService.DeltaQueryState Query,
    string Binding, int PageSize, JsonElement[] Items, JsonElement[] Changes, bool IsDelta = false);

[JsonSerializable(typeof(ODataQuerySnapshot))]
internal sealed partial class ODataQuerySnapshotJsonContext : JsonSerializerContext;
