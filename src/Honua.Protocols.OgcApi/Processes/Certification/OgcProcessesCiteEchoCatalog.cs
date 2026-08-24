// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Process catalog projected specifically onto the OGC API Processes surface.
/// </summary>
internal interface IOgcProcessesCatalog : IProcessCatalog
{
}

/// <summary>
/// Certification-profile decorator over the immutable production process catalog.
/// </summary>
internal sealed class OgcProcessesCiteEchoCatalog : IOgcProcessesCatalog
{
    private readonly IProcessCatalog _inner;
    private readonly ImmutableArray<ProcessDefinition> _all;
    private readonly bool _citeEchoEnabled;

    public OgcProcessesCiteEchoCatalog(IProcessCatalog inner, bool citeEchoEnabled)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (citeEchoEnabled
            && inner.GetProcess(OgcProcessesCiteEchoFixture.ProcessId) is not null)
        {
            throw new InvalidOperationException(
                $"The shared process catalog already contains the reserved certification process '{OgcProcessesCiteEchoFixture.ProcessId}'.");
        }

        _citeEchoEnabled = citeEchoEnabled;
        _all = citeEchoEnabled
            ? [.. inner.ListProcesses(), OgcProcessesCiteEchoFixture.Definition]
            : [.. inner.ListProcesses()];
    }

    public ProcessDefinition? GetProcess(string processId)
        => _citeEchoEnabled
            && string.Equals(processId, OgcProcessesCiteEchoFixture.ProcessId, StringComparison.Ordinal)
            ? OgcProcessesCiteEchoFixture.Definition
            : _inner.GetProcess(processId);

    public IReadOnlyList<ProcessDefinition> ListProcesses() => _all;

    public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
        => _citeEchoEnabled
            && string.Equals(category, OgcProcessesCiteEchoFixture.Definition.Category, StringComparison.Ordinal)
            ? [.. _inner.GetProcessesByCategory(category), OgcProcessesCiteEchoFixture.Definition]
            : _inner.GetProcessesByCategory(category);
}
