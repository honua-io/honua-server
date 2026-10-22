// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Npgsql;

namespace Honua.Server.Features.Spec;

// This first adapter deliberately accepts only operator-bound, encrypted PostgreSQL
// connections with literal IP destinations. Secret-store adapters, DNS destinations,
// imports, updates and unbounded materialization remain typed blockers.
internal sealed class GaLayerPublicationAdapter(
    ISecureConnectionRegistry registry,
    ISecureConnectionResolver resolver,
    ILayerPublishingService publisher,
    IConfiguration configuration)
{
    internal async Task<(string Policy, string Observation, string Verdict, int? LayerId)> ObserveAsync(
        SpecLayerIntent spec, string tenant, CancellationToken cancellationToken)
    {
        var policy = await ValidateBindingAsync(spec, tenant, cancellationToken).ConfigureAwait(false);
        var connection = await ResolveBoundConnectionAsync(spec, cancellationToken).ConfigureAwait(false);
        var layers = await publisher.ListPublishedLayersAsync(connection, spec.ServiceName, spec.ConnectionId, cancellationToken).ConfigureAwait(false);
        var matches = layers.Where(layer => layer.Schema == spec.Schema && layer.Table == spec.Table).ToArray();
        if (matches.Length > 1)
        {
            throw new GaSpecBlockedException("identity-ambiguous");
        }

        var layer = matches.SingleOrDefault();
        if (layer is not null && (layer.LayerName != spec.LayerName || !layer.Enabled))
        {
            throw new GaSpecBlockedException("existing-resource-incompatible");
        }

        long? sourceRows = null;
        if (layer is null)
        {
            sourceRows = await publisher.CountSourceRowsAsync(connection, spec.Schema, spec.Table, cancellationToken).ConfigureAwait(false);
            if (sourceRows is <= 0 or > 10000) throw new GaSpecBlockedException("bounded-publication-adapter-required");
        }

        var observation = layer is null ? Hash($"absent|{sourceRows}") : Hash(string.Join('|', layer.LayerId,
            layer.LayerName, layer.Schema, layer.Table, layer.Enabled, layer.MetadataRevision, layer.ServiceName));
        return (policy, observation, layer is null ? "create" : "noop", layer?.LayerId);
    }

    internal async Task PublishAsync(SpecLayerIntent spec, string tenant, CancellationToken cancellationToken)
    {
        await ValidateBindingAsync(spec, tenant, cancellationToken).ConfigureAwait(false);
        var connection = await ResolveBoundConnectionAsync(spec, cancellationToken).ConfigureAwait(false);
        var rows = await publisher.CountSourceRowsAsync(connection, spec.Schema, spec.Table, cancellationToken).ConfigureAwait(false);
        // No in-process async fallback or new scheduler. Larger publications must use
        // the qualified durable layer-publish job adapter in a subsequent slice.
        if (rows is <= 0 or > 10000)
        {
            throw new GaSpecBlockedException("bounded-publication-adapter-required");
        }

        await publisher.PublishLayerAsync(connection, new LayerPublishRequest
        {
            Schema = spec.Schema, Table = spec.Table, LayerName = spec.LayerName,
            ServiceName = spec.ServiceName, ConnectionId = spec.ConnectionId,
            Namespace = tenant
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveBoundConnectionAsync(SpecLayerIntent spec, CancellationToken cancellationToken)
    {
        var connection = await registry.GetConnectionAsync(spec.ConnectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new GaSpecBlockedException("connection-unavailable");
        var resolved = await resolver.ResolveConnectionStringAsync(spec.ConnectionId, cancellationToken).ConfigureAwait(false);
        var parsed = new NpgsqlConnectionStringBuilder(resolved);
        if (parsed.Host != connection.Host || parsed.Port != connection.Port || parsed.Database != connection.DatabaseName)
        {
            throw new GaSpecBlockedException("destination-binding-mismatch");
        }

        return resolved;
    }

    private async Task<string> ValidateBindingAsync(SpecLayerIntent spec, string tenant, CancellationToken cancellationToken)
    {
        var binding = configuration.GetSection($"GaSpec:ConnectionBindings:{spec.ConnectionId:D}");
        // Provisioned by an operator outside the model surface. Revision must change
        // when the tenant, approved endpoint, credentials or purpose is changed.
        if (binding["TenantId"] != tenant || binding["Purpose"] != "layer.publish" ||
            string.IsNullOrWhiteSpace(binding["Revision"]))
        {
            throw new GaSpecBlockedException("connection-binding-required");
        }

        var connection = await registry.GetConnectionAsync(spec.ConnectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null || !connection.IsActive)
        {
            throw new GaSpecBlockedException("connection-unavailable");
        }

        if (!string.IsNullOrWhiteSpace(connection.SecretRef))
        {
            throw new GaSpecBlockedException(connection.SecretType is "env" or "file"
                ? "secret-provider-prohibited" : "secret-adapter-unavailable");
        }

        if (connection.NormalizedProvider != "postgis" || !connection.IsEncrypted ||
            connection.EncryptedConnectionString.Length == 0)
        {
            throw new GaSpecBlockedException("connection-adapter-unavailable");
        }

        if (!IPAddress.TryParse(connection.Host, out _) || binding["Host"] != connection.Host ||
            binding.GetValue<int>("Port") != connection.Port || binding["Database"] != connection.DatabaseName)
        {
            throw new GaSpecBlockedException("destination-binding-mismatch");
        }

        return Hash(string.Join('|', tenant, binding["Revision"], connection.ConnectionId,
            connection.Host, connection.Port, connection.DatabaseName, connection.UpdatedAt.Ticks));
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class GaSpecBlockedException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
