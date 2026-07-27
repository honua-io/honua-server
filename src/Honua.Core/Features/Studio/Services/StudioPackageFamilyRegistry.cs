// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services.Bridging;

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
    private readonly Dictionary<StudioPackageFamily, IStudioFamilyPersistenceBridge> _bridges;

    /// <summary>
    /// Initializes a new family registry.
    /// </summary>
    /// <param name="store">The Studio package store.</param>
    /// <param name="bridgeCatalog">
    /// Optional family persistence bridges (ADR-0069, honua-server#3004). Bridged families
    /// advertise their native format, operation set, and bridge limitations.
    /// </param>
    public StudioPackageFamilyRegistry(
        IStudioPackageStore store,
        StudioFamilyPersistenceBridgeCatalog? bridgeCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _bridges = (bridgeCatalog?.Bridges ?? Array.Empty<IStudioFamilyPersistenceBridge>())
            .ToDictionary(static bridge => bridge.Family);
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
        => GetCapabilities().Families.FirstOrDefault(descriptor => descriptor.Family == family);

    private StudioPackageFamilyDescriptor Build(
        StudioPackageFamily family,
        string format,
        string validationDepth,
        bool durable)
    {
        // Bridged families (ADR-0069) advertise their native format, bridge-specific operation
        // set, publish support, and bridge limitations; persistence delegates to the family's
        // native store rather than the Studio store.
        if (_bridges.TryGetValue(family, out var bridge))
        {
            var bridgeLimitations = new List<string>(bridge.Limitations);
            if (!durable)
            {
                bridgeLimitations.Insert(0, "package lifecycle drafts are backed by in-memory storage and are not durable across server restarts");
            }

            return new StudioPackageFamilyDescriptor
            {
                Family = family,
                CurrentSchemaVersion = "1.0",
                Format = bridge.Format,
                SupportLevel = StudioPackageSupportLevel.Limited,
                SupportedOperations = bridge.SupportedOperations,
                ValidationDepth = validationDepth,
                Limitations = bridgeLimitations,
                MaxPackageBytes = DefaultMaxPackageBytes,
                PreviewSupported = true,
                PublishSupported = bridge.PublishSupported,
            };
        }
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
