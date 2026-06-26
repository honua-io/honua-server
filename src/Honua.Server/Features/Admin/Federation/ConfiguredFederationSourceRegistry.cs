// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// Configuration-backed <see cref="IFederationSourceRegistry"/>. Materializes the bound
/// <see cref="FederationSourceOptions"/> into validated <see cref="FederatedSourceDescriptor"/>
/// instances. Entries with a missing identifier or an unparsable endpoint URL are skipped so
/// a single malformed config entry cannot take the federation surface offline.
/// </summary>
internal sealed class ConfiguredFederationSourceRegistry : IFederationSourceRegistry
{
    private readonly ImmutableArray<FederatedSourceDescriptor> _sources;
    private readonly ImmutableDictionary<string, FederatedSourceDescriptor> _byId;

    public ConfiguredFederationSourceRegistry(IOptions<FederationSourceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = ImmutableArray.CreateBuilder<FederatedSourceDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in options.Value.Sources)
        {
            if (string.IsNullOrWhiteSpace(config.Id) ||
                string.IsNullOrWhiteSpace(config.RemoteLayer) ||
                !Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
                !seen.Add(config.Id))
            {
                continue;
            }

            builder.Add(new FederatedSourceDescriptor
            {
                Id = config.Id,
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Id : config.DisplayName,
                Kind = config.Kind,
                Endpoint = endpoint,
                RemoteLayer = config.RemoteLayer,
                RequestTimeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
                Enabled = config.Enabled,
            });
        }

        _sources = builder.ToImmutable();
        _byId = _sources.ToImmutableDictionary(static s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    public ImmutableArray<FederatedSourceDescriptor> GetSources() => _sources;

    public bool TryGetSource(string id, out FederatedSourceDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }
}
