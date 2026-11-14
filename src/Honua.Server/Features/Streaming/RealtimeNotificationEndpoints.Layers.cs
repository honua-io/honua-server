// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Streaming;

internal static partial class RealtimeNotificationEndpoints
{
    internal static async Task<IResult> HandleLayersAsync(HttpContext context,
        [FromServices] IRealtimeFanoutProvider provider,
        [FromServices] IMetadataV2GraphProvider graphs,
        [FromServices] IMetadataV2EnvironmentSnapshotReader history)
    {
        var identifiers = context.Request.Query["serviceId"];
        if (identifiers.Count != 1 || string.IsNullOrWhiteSpace(identifiers[0]) ||
            identifiers[0]!.Length > 256 || !TryParseCursor(context, RealtimeFamily.Catalog, out var after))
        {
            return Results.BadRequest();
        }
        var initial = await graphs.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (!initial.Index.ServicesById.TryGetValue(identifiers[0]!, out var service) &&
            !initial.Index.ServicesByName.TryGetValue(identifiers[0]!, out service))
        {
            return Results.NotFound();
        }
        var serviceId = service.Metadata.Id;
        if (!ReadableLayers(context, initial, serviceId).Any())
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        var environment = initial.Graph.Environment;
        var state = new LayerStreamState(context, serviceId, environment, graphs, history);
        await StreamAsync(context, provider, new StreamRequest(RealtimeFamily.Catalog, [environment], after,
            (item, token) => DeliverLayerAsync(state, item, token))).ConfigureAwait(false);
        return Results.Empty;
    }

    private sealed record LayerStreamState(HttpContext Context, string ServiceId, string Environment,
        IMetadataV2GraphProvider Graphs, IMetadataV2EnvironmentSnapshotReader History);

    private static async Task<bool> DeliverLayerAsync(LayerStreamState state, RealtimeEvent item, CancellationToken token)
    {
        if (item.Kind != "catalog.changed" || item.ScopeKey != state.Environment)
        {
            return true;
        }
        var etag = item.Payload.GetProperty("etag").GetString()!;
        var previousEtag = item.Payload.GetProperty("previousEtag").GetString();
        var next = await state.History.GetByEtagAsync(state.Environment, etag, token).ConfigureAwait(false);
        var previous = previousEtag is null ? null :
            await state.History.GetByEtagAsync(state.Environment, previousEtag, token).ConfigureAwait(false);
        if (next is null || (previousEtag is not null && previous is null))
        {
            await WriteFrameAsync(state.Context.Response, new StreamFrame(null, "reset", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("reason", "retention");
                writer.WriteNumber("head", item.Sequence);
                writer.WriteEndObject();
            }), token).ConfigureAwait(false);
            return false;
        }
        var changed = ChangedLayers(previous, next, state.ServiceId);
        // Resolve current permissions even when replaying older definitions.
        var current = await state.Graphs.GetCurrentAsync(token).ConfigureAwait(false);
        var readable = ReadableLayers(state.Context, current, state.ServiceId).ToHashSet();
        var visibleChanges = changed.Where(readable.Contains).ToArray();
        for (var i = 0; i < visibleChanges.Length; i++)
        {
            // Commit the catalog cursor only after its final layer frame. A disconnect
            // midway through a catalog change can repeat frames, but cannot skip layers.
            RealtimePosition? cursor = i == visibleChanges.Length - 1 ? item.Position : null;
            await WriteFrameAsync(state.Context.Response, new StreamFrame(cursor, "layer.changed", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("serviceId", state.ServiceId);
                writer.WriteNumber("layerId", visibleChanges[i]);
                writer.WriteString("etag", etag);
                writer.WriteEndObject();
            }), token).ConfigureAwait(false);
        }
        return true;
    }

    internal static int[] ChangedLayers(MetadataV2GraphSnapshot? previous,
        MetadataV2GraphSnapshot next, string serviceId)
    {
        var before = previous is null ? new Dictionary<int, string>() : LayerDefinitions(previous, serviceId);
        var after = LayerDefinitions(next, serviceId);
        return before.Keys.Union(after.Keys).Where(id =>
            !before.TryGetValue(id, out var oldDefinition) ||
            !after.TryGetValue(id, out var newDefinition) || oldDefinition != newDefinition).Order().ToArray();
    }

    private static Dictionary<int, string> LayerDefinitions(MetadataV2GraphSnapshot snapshot, string serviceId)
    {
        if (!snapshot.Index.ServicesById.TryGetValue(serviceId, out var service))
        {
            return [];
        }
        return snapshot.Index.PublicationsByService[serviceId]
            .Where(publication => snapshot.IsRoutable(publication))
            .Select(publication => (Publication: publication, Id: publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication)))
            .Where(item => item.Id.HasValue)
            .GroupBy(item => item.Id!.Value)
            .ToDictionary(group => group.Key, group =>
                JsonSerializer.Serialize(service, MetadataV2JsonContext.Default.MetadataV2Service) + "\n" +
                string.Join("\n", group.OrderBy(item => item.Publication.Metadata.Id, StringComparer.Ordinal).Select(item =>
                    JsonSerializer.Serialize(item.Publication, MetadataV2JsonContext.Default.MetadataV2Publication) + "\n" +
                    JsonSerializer.Serialize(snapshot.ResolveResource(item.Publication), MetadataV2JsonContext.Default.MetadataV2Resource))));
    }

    private static IEnumerable<int> ReadableLayers(HttpContext context, MetadataV2GraphSnapshot snapshot, string serviceId)
    {
        if (!snapshot.Index.ServicesById.TryGetValue(serviceId, out var service))
        {
            yield break;
        }
        foreach (var publication in snapshot.Index.PublicationsByService[serviceId])
        {
            var resource = snapshot.ResolveResource(publication);
            var layerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication);
            if (resource is not null && layerId.HasValue && snapshot.IsRoutable(publication) &&
                AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
            {
                yield return layerId.Value;
            }
        }
    }
}
