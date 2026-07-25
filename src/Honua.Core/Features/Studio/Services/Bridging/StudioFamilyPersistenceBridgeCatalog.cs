// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Studio.Services.Bridging;

/// <summary>
/// Resolve-time composition of the available ADR-0069 family persistence bridges
/// (honua-server#3004). Bridge availability is decided when the catalog is resolved — not when
/// services are registered — because native stores (<see cref="IFormPackageStore"/>,
/// <see cref="IAnalysisContentStore"/>) are registered at different points across hosts (the
/// Postgres composition root, in-memory fallbacks, and test fixtures that rewire providers
/// after the application's own registrations). A family whose native store is absent simply has
/// no bridge and keeps today's Studio-store behavior.
/// </summary>
public sealed class StudioFamilyPersistenceBridgeCatalog
{
    /// <summary>
    /// Composes the catalog from the current scope's registered native stores.
    /// </summary>
    public StudioFamilyPersistenceBridgeCatalog(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var bridges = new List<IStudioFamilyPersistenceBridge>(2);

        var formStore = serviceProvider.GetService<IFormPackageStore>();
        var formValidator = serviceProvider.GetService<FormPackageValidator>();
        if (formStore is not null && formValidator is not null)
        {
            bridges.Add(new FormStudioPackageBridge(formStore, formValidator));
        }

        var analysisStore = serviceProvider.GetService<IAnalysisContentStore>();
        if (analysisStore is not null)
        {
            bridges.Add(new AnalysisStudioPackageBridge(
                analysisStore,
                serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System));
        }

        Bridges = bridges;
    }

    /// <summary>
    /// Testing constructor composing an explicit bridge set.
    /// </summary>
    public StudioFamilyPersistenceBridgeCatalog(IReadOnlyList<IStudioFamilyPersistenceBridge> bridges)
    {
        ArgumentNullException.ThrowIfNull(bridges);
        Bridges = bridges;
    }

    /// <summary>Available bridges, at most one per family.</summary>
    public IReadOnlyList<IStudioFamilyPersistenceBridge> Bridges { get; }
}
