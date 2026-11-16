// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Studio.Services.Bridging;

public sealed class StudioFamilyPersistenceBridgeCatalog
{
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

    public StudioFamilyPersistenceBridgeCatalog(IReadOnlyList<IStudioFamilyPersistenceBridge> bridges)
    {
        ArgumentNullException.ThrowIfNull(bridges);
        Bridges = bridges;
    }

    public IReadOnlyList<IStudioFamilyPersistenceBridge> Bridges { get; }
}
