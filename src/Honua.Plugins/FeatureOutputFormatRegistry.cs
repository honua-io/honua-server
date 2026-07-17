// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Reflection;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Plugins;

/// <summary>
/// Default <see cref="IFeatureOutputFormatRegistry"/>. Aggregates the registered plugin
/// <see cref="IFeatureOutputFormat"/>s, keys them by their case-insensitive wire token, and gates
/// resolution behind the Enterprise <c>plugin.sdk</c> entitlement plus the operator kill-switch —
/// when unlicensed or disabled it advertises nothing and resolves nothing. Duplicate or built-in
/// colliding tokens are rejected at construction so a misconfigured plugin fails fast rather than
/// silently shadowing another format.
/// </summary>
internal sealed class FeatureOutputFormatRegistry : IFeatureOutputFormatRegistry
{
    // Wire tokens owned by the built-in export writers; a plugin may not shadow these.
    private static readonly FrozenSet<string> ReservedFormatIds =
        new[] { "csv", "shapefile", "gpkg", "geojson", "json", "pbf" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<string, IFeatureOutputFormat> _formats;
    private readonly PluginOutputFormatDescriptor[] _advertised;
    private readonly ILicenseEntitlementService _licensing;
    private readonly bool _enabledByConfig;

    public FeatureOutputFormatRegistry(
        IEnumerable<IFeatureOutputFormat> formats,
        ILicenseEntitlementService licensing,
        IOptions<PluginOptions> options)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(options);
        _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
        _enabledByConfig = options.Value.Enabled;

        var map = new Dictionary<string, IFeatureOutputFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in formats)
        {
            var id = format.FormatId;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    $"Plugin output format '{format.GetType().FullName}' declares an empty FormatId.");
            }

            if (ReservedFormatIds.Contains(id))
            {
                throw new InvalidOperationException(
                    $"Plugin output format '{PluginIdOf(format)}' uses the reserved format id '{id}'. "
                    + "Choose a distinct token that does not collide with a built-in format.");
            }

            if (!map.TryAdd(id, format))
            {
                throw new InvalidOperationException(
                    $"Two plugin output formats declare the same format id '{id}'. Format ids must be unique.");
            }
        }

        _formats = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _advertised = _formats.Values
            .Select(f => new PluginOutputFormatDescriptor(f.FormatId, f.MediaType, f.FileExtension, PluginIdOf(f)))
            .ToArray();
    }

    /// <inheritdoc />
    public bool HasFormats => _enabledByConfig && _formats.Count > 0 && IsLicensed;

    /// <inheritdoc />
    public IReadOnlyCollection<PluginOutputFormatDescriptor> AdvertisedFormats
        => HasFormats ? _advertised : [];

    /// <inheritdoc />
    public bool TryGetFormat(string formatId, out IFeatureOutputFormat? format)
    {
        format = null;
        if (string.IsNullOrWhiteSpace(formatId) || !HasFormats)
        {
            return false;
        }

        return _formats.TryGetValue(formatId, out format);
    }

    private bool IsLicensed => _licensing.CheckEntitlement(FeatureCatalog.PluginSdkKey).IsActive;

    private static string PluginIdOf(object instance)
    {
        var type = instance.GetType();
        return type.GetCustomAttribute<PluginAttribute>()?.Id ?? type.FullName ?? type.Name;
    }
}
