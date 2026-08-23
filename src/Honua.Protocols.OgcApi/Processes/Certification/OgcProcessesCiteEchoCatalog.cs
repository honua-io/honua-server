// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Certification-profile decorator over the immutable production process catalog.
/// </summary>
internal sealed class OgcProcessesCiteEchoCatalog : IProcessCatalog
{
    private readonly BuiltInProcessCatalog _inner;
    private readonly ImmutableArray<ProcessDefinition> _all;

    public OgcProcessesCiteEchoCatalog(BuiltInProcessCatalog inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _all = [.. inner.ListProcesses(), OgcProcessesCiteEchoFixture.Definition];
    }

    public ProcessDefinition? GetProcess(string processId)
        => string.Equals(processId, OgcProcessesCiteEchoFixture.ProcessId, StringComparison.Ordinal)
            ? OgcProcessesCiteEchoFixture.Definition
            : _inner.GetProcess(processId);

    public IReadOnlyList<ProcessDefinition> ListProcesses() => _all;

    public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
        => string.Equals(category, OgcProcessesCiteEchoFixture.Definition.Category, StringComparison.Ordinal)
            ? [.. _inner.GetProcessesByCategory(category), OgcProcessesCiteEchoFixture.Definition]
            : _inner.GetProcessesByCategory(category);
}
