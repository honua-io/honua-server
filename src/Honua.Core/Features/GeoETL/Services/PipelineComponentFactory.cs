// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.GeoETL.Abstractions;

namespace Honua.Core.Features.GeoETL.Services;

/// <summary>
/// Resolves pipeline components by type discriminator from the registered connector and
/// transform sets. Built once over the DI-provided enumerables, mirroring the
/// geoprocessing dispatch-by-id pattern.
/// </summary>
public sealed class PipelineComponentFactory : IPipelineComponentFactory
{
    private readonly FrozenDictionary<string, IPipelineSourceConnector> _sources;
    private readonly FrozenDictionary<string, IPipelineTransform> _transforms;
    private readonly FrozenDictionary<string, IPipelineSinkConnector> _sinks;

    /// <summary>
    /// Builds the factory over the registered components.
    /// </summary>
    /// <param name="sources">Registered source connectors.</param>
    /// <param name="transforms">Registered transforms.</param>
    /// <param name="sinks">Registered sink connectors.</param>
    public PipelineComponentFactory(
        IEnumerable<IPipelineSourceConnector> sources,
        IEnumerable<IPipelineTransform> transforms,
        IEnumerable<IPipelineSinkConnector> sinks)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(transforms);
        ArgumentNullException.ThrowIfNull(sinks);

        _sources = sources.ToFrozenDictionary(c => c.Type, StringComparer.Ordinal);
        _transforms = transforms.ToFrozenDictionary(t => t.Type, StringComparer.Ordinal);
        _sinks = sinks.ToFrozenDictionary(c => c.Type, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IPipelineSourceConnector? ResolveSource(string type)
        => _sources.TryGetValue(type, out var connector) ? connector : null;

    /// <inheritdoc />
    public IPipelineTransform? ResolveTransform(string type)
        => _transforms.TryGetValue(type, out var transform) ? transform : null;

    /// <inheritdoc />
    public IPipelineSinkConnector? ResolveSink(string type)
        => _sinks.TryGetValue(type, out var connector) ? connector : null;
}
