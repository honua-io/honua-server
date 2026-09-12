// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Reads the edit capabilities a Metadata v2 publication declares.
/// </summary>
/// <remarks>
/// Declared capabilities are an editing contract rather than advisory text: a protocol that
/// accepts an edit kind the publication never declared exposes a broader surface than clients
/// were told exists (honua-server#4073 for the Esri GeoServices FeatureServer surface,
/// honua-server#4707 for OGC API Features). Both write surfaces resolve capabilities through
/// this one helper so they cannot drift apart.
/// </remarks>
public static class MetadataV2EditCapabilities
{
    /// <summary>Capability token for feature creation.</summary>
    public const string Create = "Create";

    /// <summary>Capability token for feature update.</summary>
    public const string Update = "Update";

    /// <summary>Capability token for feature deletion.</summary>
    public const string Delete = "Delete";

    /// <summary>Umbrella capability token that implies <see cref="Create"/>, <see cref="Update"/> and <see cref="Delete"/>.</summary>
    public const string Editing = "Editing";

    /// <summary>
    /// Capability set assumed when neither the publication nor its service declares one.
    /// </summary>
    private static readonly string[] _queryOnly = ["Query"];

    /// <summary>
    /// Resolves the capability tokens a publication actually declares: its own capabilities
    /// when it has any, otherwise the ones declared by the hosting service.
    /// </summary>
    /// <param name="service">Service hosting the publication.</param>
    /// <param name="publication">Publication whose capabilities are being resolved, when any.</param>
    /// <returns>The declared capability tokens, or <see langword="null"/> when neither the
    /// publication nor the service declares any. A null result means "this metadata makes no
    /// statement", which is different from a declared read-only set.</returns>
    public static IReadOnlyList<string>? ResolveDeclared(MetadataV2Service service, MetadataV2Publication? publication)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (publication?.Capabilities is { Count: > 0 } publicationCapabilities)
        {
            return publicationCapabilities;
        }

        if (service.Options.TryGetValue("capabilities", out var element) &&
            element.ValueKind == JsonValueKind.Array)
        {
            var declared = element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray();
            if (declared.Length > 0)
            {
                return declared;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the capability tokens in force for a publication, falling back to the
    /// read-only default when the metadata declares none.
    /// </summary>
    /// <param name="service">Service hosting the publication.</param>
    /// <param name="publication">Publication whose capabilities are being resolved, when any.</param>
    /// <returns>The capability tokens in force. Never empty.</returns>
    public static IReadOnlyList<string> Resolve(MetadataV2Service service, MetadataV2Publication? publication)
        => ResolveDeclared(service, publication) ?? _queryOnly;

    /// <summary>
    /// Determines whether the publication supports the supplied operation, treating metadata
    /// that declares no capabilities as read-only.
    /// </summary>
    /// <param name="service">Service hosting the publication.</param>
    /// <param name="publication">Publication being checked, when any.</param>
    /// <param name="operation">Capability token being checked (e.g. <see cref="Create"/>).</param>
    /// <returns><see langword="true"/> when the operation is supported.</returns>
    public static bool Supports(MetadataV2Service service, MetadataV2Publication? publication, string operation)
        => Contains(Resolve(service, publication), operation);

    /// <summary>
    /// Determines whether the publication <em>declares</em> the supplied operation.
    /// </summary>
    /// <param name="service">Service hosting the publication.</param>
    /// <param name="publication">Publication being checked, when any.</param>
    /// <param name="operation">Capability token being checked (e.g. <see cref="Create"/>).</param>
    /// <returns><see langword="true"/> when a declared capability set contains the operation,
    /// <see langword="false"/> when a declared set omits it, and <see langword="null"/> when
    /// the metadata declares no capabilities at all. Callers that enforce the contract must
    /// only reject on <see langword="false"/>: the v1-compatibility metadata path projects no
    /// capabilities onto OGC API Features publications, and treating that silence as a denial
    /// would take editing away from deployments that never declared anything.</returns>
    public static bool? SupportsDeclared(MetadataV2Service service, MetadataV2Publication? publication, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var declared = ResolveDeclared(service, publication);
        return declared is null ? null : Contains(declared, operation);
    }

    private static bool Contains(IReadOnlyList<string> capabilities, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        for (var index = 0; index < capabilities.Count; index++)
        {
            var capability = capabilities[index];
            if (capability.Equals(operation, StringComparison.OrdinalIgnoreCase) ||
                capability.Equals(Editing, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
