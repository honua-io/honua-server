// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Infrastructure.Helpers;
using Honua.ControlPlane;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal;

// Registers the heavyweight GDAL worker. The worker reuses the durable
// job-execution substrate from Honua.Jobs (the same
// JobExecutionService claim/lease/heartbeat loop and PostgreSQL job
// stores) rather than forking a parallel runtime — it simply
// registers a native-profile IJobExecutor set and lets the shared
// loop dispatch to it. GDAL is reached through the gdalwarp / ogr2ogr
// CLI tools shipped in the worker image base layer; no managed GDAL bindings or
// native package references are introduced.
internal static class GdalWorkerServiceCollectionExtensions
{
    internal static void AddLicenseSecretResolvers(IServiceCollection services, IConfiguration configuration)
    {
        if (LicenseOptions.ParseMode(configuration[$"{LicenseOptions.SectionName}:Mode"]) == LicenseMode.Disabled)
        {
            return;
        }

        Honua.Cloud.Aws.Features.Licensing.AwsLicenseSecretResolverServiceCollectionExtensions
            .AddAwsLicenseSecretResolver(services, configuration);
        Honua.Licensing.AzureLicenseSecretResolverServiceCollectionExtensions
            .AddAzureLicenseSecretResolver(services, configuration);
    }

    // Wires the GDAL worker host: the system database connection, the shared durable execution
    // substrate (queue, job store, log store, cancellation registry), the two
    // substrate hosted services (execution + reconciliation), and the
    // native-profile GDAL executors.
    public static IServiceCollection AddGdalWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<LicenseOptions>(configuration.GetSection(LicenseOptions.SectionName));
        if (LicenseOptions.ParseMode(configuration[$"{LicenseOptions.SectionName}:Mode"]) == LicenseMode.Disabled)
        {
            services.TryAddSingleton<DisabledLicenseService>();
            services.TryAddSingleton<ILicenseOperationPolicy>(sp => sp.GetRequiredService<DisabledLicenseService>());
            services.TryAddSingleton<ILicenseEntitlementService>(sp => sp.GetRequiredService<DisabledLicenseService>());
            services.TryAddSingleton<ILicenseStatusProvider>(sp => sp.GetRequiredService<DisabledLicenseService>());
            services.TryAddSingleton<ILicenseManager>(sp => sp.GetRequiredService<DisabledLicenseService>());
        }
        else
        {
            AddLicenseSecretResolvers(services, configuration);
            services.TryAddSingleton<IEd25519Verifier, BouncyCastleEd25519Verifier>();
            services.TryAddSingleton<FileBackedLicenseService>();
            services.TryAddSingleton<ILicenseOperationPolicy>(sp => sp.GetRequiredService<FileBackedLicenseService>());
            services.AddHostedService(sp => sp.GetRequiredService<FileBackedLicenseService>());
        }

        services.AddJobHostSystemDataSource(configuration);
        services.TryAddPostgresExecutionJobStore(configuration);
        services.TryAddPostgresJobQueueAndLogStore();

        // Worker-side execution loop dependencies and the two hosted services. This
        // is the SAME JobExecutionService / JobReconciliationService the substrate
        // ships; the worker does not introduce its own BackgroundService.
        services.AddGdalWorkerExecutionLoop();

        // Opt-in staged output publication (#3089): when Geoprocessing:OutputStaging
        // is enabled, large outputs stream to the shared object store and only typed
        // references enter the durable job record.
        services.AddGeoprocessingOutputStaging(configuration);

        // GDAL executor options, CLI runner, and the native-profile executor set.
        // The per-process executors implement IProcessExecutor and self-declare their
        // process id(s) (GP Devkit authoring contract #2122), so GdalDispatchJobExecutor
        // builds its routing table by enumerating IProcessExecutor instead of a
        // hand-maintained ctor. Shared with the store-free AddGdalProcessExecutors seam.
        services.AddGdalProcessExecutors(configuration);

        // Register the native dispatcher as the single IJobExecutor for the
        // Geoprocessing kind in this host. It declares AcceptedRuntimeProfiles =
        // { "native" }, so JobExecutionService passes that profile set to the
        // queue claim filter and the worker only claims native-profile jobs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IJobExecutor, GdalDispatchJobExecutor>());

        return services;
    }

    // Registers ONLY the native GDAL IProcessExecutor set plus its
    // dependencies — the GdalWorkerOptions, the IGdalCommandRunner
    // CLI runner, and each per-process executor — with NO database connection, queue,
    // job store, or hosted execution loop. This is the store-free seam the GP Devkit
    // headless runner / honua gp dev CLI consume so a developer
    // can run a single native op directly without the durable substrate
    // AddGdalWorker wires for the worker host.
    public static IServiceCollection AddGdalProcessExecutors(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddGdalProcessExecutors(configuration, GdalProcessExecutorMode.InProcess);

    public static IServiceCollection AddGdalProcessExecutors(
        this IServiceCollection services,
        IConfiguration configuration,
        GdalProcessExecutorMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GdalWorkerOptions>()
            .Bind(configuration.GetSection(GdalWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ConfigurationBinder APPENDS array/list items onto a pre-populated list rather
        // than replacing it, so a bound AllowedRasterInputFormats would only ever GROW
        // the default TIFF/PNG/JPEG allowlist — an operator could never TIGHTEN it to a
        // narrower set (e.g. TIFF-only). When the config actually supplies the list, REPLACE
        // the bound defaults with precisely the configured values.
        services.PostConfigure<GdalWorkerOptions>(options =>
            ReplaceListWhenConfigured(
                configuration
                    .GetSection(GdalWorkerOptions.SectionName)
                    .GetSection(nameof(GdalWorkerOptions.AllowedRasterInputFormats)),
                options.AllowedRasterInputFormats));

        // Restrictive-by-default GDAL runtime hardening (#2765): the driver-skip and
        // remote-VSI-disable policy every GDAL/OGR subprocess inherits. Bound here so
        // both the in-process and container-exec runner seams resolve it.
        services
            .AddOptions<GdalHardeningOptions>()
            .Bind(configuration.GetSection(GdalHardeningOptions.SectionName));

        // Same append-vs-replace hazard as above: SkipDrivers ships ~30 default denials,
        // and Bind would only ADD to them — an operator could never REMOVE a default skip
        // driver (the documented way to actually open a bomb-capable format alongside an
        // AllowedRasterInputFormats opt-in). Replace with the configured set when present.
        services.PostConfigure<GdalHardeningOptions>(options =>
            ReplaceListWhenConfigured(
                configuration
                    .GetSection(GdalHardeningOptions.SectionName)
                    .GetSection(nameof(GdalHardeningOptions.SkipDrivers)),
                options.SkipDrivers));

        // Registered raster references carry only the logical bucket/key identity.
        // Project the execution-owned S3 endpoint configuration into the worker so
        // GDAL resolves /vsis3 paths against the same S3-compatible store the API
        // probed (for example MinIO or LocalStack), without serializing an endpoint
        // URL or credentials into the durable job descriptor.
        services
            .AddOptions<AwsS3Options>()
            .Bind(configuration.GetSection("FileStorage:AwsS3"));
        services.PostConfigure<AwsS3Options>(options =>
        {
            options.AccessKeyId = SecretReferenceResolver.ResolveEnvironmentReference(
                options.AccessKeyId,
                "FileStorage:AwsS3:AccessKeyId");
            options.SecretAccessKey = SecretReferenceResolver.ResolveEnvironmentReference(
                options.SecretAccessKey,
                "FileStorage:AwsS3:SecretAccessKey");
        });
        services
            .AddOptions<AzureBlobOptions>()
            .Bind(configuration.GetSection("FileStorage:AzureBlob"));
        services.PostConfigure<AzureBlobOptions>(options =>
            options.ConnectionString = SecretReferenceResolver.ResolveEnvironmentReference(
                options.ConnectionString,
                "FileStorage:AzureBlob:ConnectionString") ?? string.Empty);

        if (mode == GdalProcessExecutorMode.Container)
        {
            // Container-exec fidelity path: bind the GdalContainer options and run each
            // GDAL tool inside the real worker image. The in-process runner is NOT
            // registered, so the same executor code path shells out via docker instead.
            services
                .AddOptions<GdalContainerExecutionOptions>()
                .Bind(configuration.GetSection("GdalContainer"));
            services.TryAddSingleton<IDockerCommandInvoker, ProcessDockerCommandInvoker>();
            services.TryAddSingleton<IGdalCommandRunner, DockerGdalCommandRunner>();
        }
        else
        {
            services.TryAddSingleton<IGdalCommandRunner, ProcessGdalCommandRunner>();
        }

        RegisterGdalExecutor<GdalVectorConvertJobExecutor>(services);
        RegisterGdalExecutor<GdalVectorReprojectJobExecutor>(services);
        RegisterGdalExecutor<GdalVectorSourceReadJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterReprojectJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterResampleJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterInterpolateJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterMosaicJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterMapAlgebraJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterSpectralIndexJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterReclassifyJobExecutor>(services);
        RegisterGdalExecutor<GdalProximityJobExecutor>(services);
        RegisterGdalExecutor<GdalContourJobExecutor>(services);
        RegisterGdalExecutor<GdalViewshedJobExecutor>(services);
        RegisterGdalExecutor<GdalPolygonizeJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterizeJobExecutor>(services);
        RegisterGdalExecutor<GdalSurfaceJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterClipJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterReprojectCatalogJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterFormatConvertJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterStatisticsJobExecutor>(services);
        RegisterGdalExecutor<GdalRasterZonalStatisticsJobExecutor>(services);
        RegisterGdalExecutor<GdalMultidimCoverageMetadataJobExecutor>(services);
        RegisterGdalExecutor<PdalPointCloudConvertJobExecutor>(services);

        return services;
    }

    public static IServiceCollection AddGlassBoxGdalCapture(
        this IServiceCollection services,
        GlassBoxCapture capture)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(capture);

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IGdalCommandRunner))
            ?? throw new InvalidOperationException(
                "AddGlassBoxGdalCapture requires AddGdalProcessExecutors to have registered an IGdalCommandRunner first.");

        services.Remove(inner);
        services.AddSingleton(capture);
        services.AddSingleton<IGdalCommandRunner>(sp =>
            new GlassBoxGdalCommandRunner(MaterializeInner(sp, inner), capture));

        return services;
    }

    private static void ReplaceListWhenConfigured(IConfigurationSection section, IList<string> target)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            // No config for this list — keep the bound-in defaults as-is.
            return;
        }

        target.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Not a .Select(...) candidate: seen.Add(trimmed) is the dedup side effect
        // itself, so a projection here would double as the mutation.
        foreach (var value in (children).Select(child => child.Value))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                target.Add(trimmed);
            }
        }
    }

    private static IGdalCommandRunner MaterializeInner(IServiceProvider sp, ServiceDescriptor inner)
    {
        if (inner.ImplementationInstance is IGdalCommandRunner instance)
        {
            return instance;
        }

        if (inner.ImplementationFactory is not null)
        {
            return (IGdalCommandRunner)inner.ImplementationFactory(sp);
        }

        var implementationType = inner.ImplementationType
            ?? throw new InvalidOperationException("The registered IGdalCommandRunner has no resolvable implementation.");
        return (IGdalCommandRunner)ActivatorUtilities.CreateInstance(sp, implementationType);
    }

    private static IServiceCollection AddGdalWorkerExecutionLoop(this IServiceCollection services)
    {
        services.TryAddSingleton<ExecutionJobCancellationTokens>();
        services.TryAddSingleton<IJobCancellationNotifier>(
            sp => sp.GetRequiredService<ExecutionJobCancellationTokens>());

        services.AddHostedService<JobExecutionService>();
        services.AddHostedService<JobReconciliationService>();

        return services;
    }

    private static void RegisterGdalExecutor<TExecutor>(IServiceCollection services)
        where TExecutor : class, IProcessExecutor
    {
        services.TryAddSingleton<TExecutor>();

        if (!services.Any(d =>
                d.ServiceType == typeof(IProcessExecutor)
                && d.ImplementationFactory is not null
                && d.ImplementationFactory.Method.ReturnType == typeof(TExecutor)))
        {
            services.AddSingleton<IProcessExecutor>(sp => sp.GetRequiredService<TExecutor>());
        }
    }
}
