// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Plugins;

/// <summary>
/// Default <see cref="IComputedFieldPipeline"/>. Aggregates the registered
/// <see cref="IComputedFieldProvider"/>s, runs them in deterministic plugin-id order at the query
/// projection seam, and gates the whole thing behind the Enterprise <c>plugin.sdk</c> entitlement
/// plus the operator kill-switch. When unlicensed/disabled it returns features unchanged.
/// Provider failures are isolated and logged — a faulting computed field is omitted, never failing
/// the query.
/// </summary>
internal sealed partial class ComputedFieldPipeline : IComputedFieldPipeline
{
    private readonly IComputedFieldProvider[] _providers;
    private readonly ILicenseEntitlementService _licensing;
    private readonly ILogger<ComputedFieldPipeline> _logger;
    private readonly bool _enabledByConfig;

    public ComputedFieldPipeline(
        IEnumerable<IComputedFieldProvider> providers,
        ILicenseEntitlementService licensing,
        IOptions<PluginOptions> options,
        ILogger<ComputedFieldPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _providers = OrderByPluginId(providers);
        _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabledByConfig = options.Value.Enabled;
    }

    /// <inheritdoc />
    public bool HasComputedFields => _enabledByConfig && _providers.Length > 0;

    /// <inheritdoc />
    public async ValueTask<Feature> ProjectAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HasComputedFields || !_licensing.CheckEntitlement(FeatureCatalog.PluginSdkKey).IsActive)
        {
            return feature;
        }

        var attributes = feature.Attributes;
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await provider.ComputeAsync(feature, context, cancellationToken).ConfigureAwait(false);
                attributes = attributes.SetItem(provider.FieldName, value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Computed fields are best-effort on the read path: isolate and omit.
                Log.ComputeFailed(_logger, PluginIdOf(provider), provider.FieldName, context.ServiceId, context.LayerId, ex);
            }
        }

        return ReferenceEquals(attributes, feature.Attributes)
            ? feature
            : feature with { Attributes = attributes };
    }

    private static IComputedFieldProvider[] OrderByPluginId(IEnumerable<IComputedFieldProvider> instances)
        => [.. instances.OrderBy(PluginIdOf, StringComparer.Ordinal)];

    private static string PluginIdOf(object instance)
    {
        var type = instance.GetType();
        return type.GetCustomAttribute<PluginAttribute>()?.Id ?? type.FullName ?? type.Name;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9310, Level = LogLevel.Error,
            Message = "Plugin computed-field provider '{PluginId}' failed computing '{FieldName}' for service {ServiceId} layer {LayerId}; field omitted.")]
        public static partial void ComputeFailed(ILogger logger, string pluginId, string fieldName, string serviceId, int layerId, Exception exception);
    }
}
