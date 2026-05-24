// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Default package-family registry for the Studio lifecycle.
/// </summary>
public sealed class StudioPackageFamilyRegistry : IStudioPackageFamilyRegistry
{
    private const int DefaultMaxPackageBytes = 1_048_576;
    private static readonly StudioPackageOperation[] AllLifecycleOperations =
    [
        StudioPackageOperation.DraftCreate,
        StudioPackageOperation.DraftRead,
        StudioPackageOperation.DraftUpdate,
        StudioPackageOperation.Validate,
        StudioPackageOperation.PreviewPlan,
        StudioPackageOperation.ContentVersionCreate,
        StudioPackageOperation.ContentVersionRead,
        StudioPackageOperation.ContentVersionCompare,
        StudioPackageOperation.PublishRequestCreate,
        StudioPackageOperation.Reopen,
        StudioPackageOperation.Rollback,
    ];

    private readonly IStudioPackageStore _store;

    /// <summary>
    /// Initializes a new family registry.
    /// </summary>
    public StudioPackageFamilyRegistry(IStudioPackageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public StudioPackageFamilyCapabilities GetCapabilities()
    {
        var durable = _store.PersistenceMode == StudioPackagePersistenceMode.Durable;
        return new StudioPackageFamilyCapabilities
        {
            PersistenceMode = _store.PersistenceMode,
            Durable = durable,
            Families =
            [
                Build(StudioPackageFamily.Query, "studio_query_package.v1", "envelope", durable),
                Build(StudioPackageFamily.Analysis, "studio_analysis_package.v1", "envelope", durable),
                Build(StudioPackageFamily.Map, "honua_map_package.v1", "family-specific", durable),
                Build(StudioPackageFamily.Dashboard, "studio_dashboard_package.v1", "envelope", durable),
                Build(StudioPackageFamily.Report, "studio_report_package.v1", "envelope", durable),
                Build(StudioPackageFamily.Form, "studio_form_package.v1", "envelope", durable),
                Build(StudioPackageFamily.App, "honua_app_package.v1", "family-specific", durable),
                Build(StudioPackageFamily.Workflow, "studio_workflow_package.v1", "envelope", durable),
                Build(StudioPackageFamily.Geoprocessing, "studio_gp_package.v1", "envelope", durable),
                Build(StudioPackageFamily.Etl, "studio_etl_package.v1", "envelope", durable),
            ],
        };
    }

    /// <inheritdoc />
    public StudioPackageFamilyDescriptor? GetDescriptor(StudioPackageFamily family)
    {
        foreach (var descriptor in GetCapabilities().Families)
        {
            if (descriptor.Family == family)
            {
                return descriptor;
            }
        }

        return null;
    }

    private static StudioPackageFamilyDescriptor Build(
        StudioPackageFamily family,
        string format,
        string validationDepth,
        bool durable)
    {
        var supportLevel = durable
            ? validationDepth == "family-specific"
                ? StudioPackageSupportLevel.Supported
                : StudioPackageSupportLevel.Limited
            : StudioPackageSupportLevel.Limited;

        var limitations = new List<string>();
        if (!durable)
        {
            limitations.Add("package lifecycle is backed by in-memory storage and is not durable across server restarts");
        }
        if (validationDepth == "envelope")
        {
            limitations.Add("family-specific deep validation is deferred; envelope validation is active");
        }

        return new StudioPackageFamilyDescriptor
        {
            Family = family,
            CurrentSchemaVersion = "1.0",
            Format = format,
            SupportLevel = supportLevel,
            SupportedOperations = AllLifecycleOperations,
            ValidationDepth = validationDepth,
            Limitations = limitations,
            MaxPackageBytes = DefaultMaxPackageBytes,
            PreviewSupported = true,
            PublishSupported = true,
        };
    }
}
