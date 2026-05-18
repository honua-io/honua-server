// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Projections;

/// <summary>
/// Evaluates catalog metadata against projection target requirements.
/// </summary>
public static class ProjectionReadinessEvaluator
{
    public static ProjectionReadinessResult Evaluate(CatalogMetadataSemantics metadata, MetadataProjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var definition = MetadataProjectionTargets.Get(target);
        var satisfied = new List<ProjectionRequirement>();
        var missingRequired = new List<ProjectionRequirement>();
        var missingRecommended = new List<ProjectionRequirement>();

        foreach (var requirement in definition.Requirements)
        {
            if (IsAvailable(metadata, requirement.Semantic))
            {
                satisfied.Add(requirement);
                continue;
            }

            if (requirement.Importance == ProjectionRequirementImportance.Required)
            {
                missingRequired.Add(requirement);
            }
            else
            {
                missingRecommended.Add(requirement);
            }
        }

        return new ProjectionReadinessResult(
            definition.Target,
            definition.Label,
            definition.Slug,
            missingRequired.Count == 0,
            satisfied,
            missingRequired,
            missingRecommended);
    }

    public static IReadOnlyList<ProjectionReadinessResult> EvaluateAll(CatalogMetadataSemantics metadata) =>
        MetadataProjectionTargets.All
            .Select(definition => Evaluate(metadata, definition.Target))
            .ToArray();

    private static bool IsAvailable(CatalogMetadataSemantics metadata, ProjectionMetadataSemantic semantic) =>
        semantic switch
        {
            ProjectionMetadataSemantic.PrimaryIdentifier => HasPrimaryIdentifier(metadata),
            ProjectionMetadataSemantic.Title => HasText(metadata.Title) || HasRole(metadata, FieldSemanticRoleVocabulary.DisplayTitle),
            ProjectionMetadataSemantic.Summary => HasText(metadata.Summary),
            ProjectionMetadataSemantic.Description => HasText(metadata.Description),
            ProjectionMetadataSemantic.Contact => metadata.Contacts.Any(contact => HasText(contact.Name)),
            ProjectionMetadataSemantic.License => HasLicense(metadata),
            ProjectionMetadataSemantic.Rights => HasText(metadata.Rights?.RightsStatement) || HasText(metadata.Rights?.Attribution),
            ProjectionMetadataSemantic.CreatedDate => HasDate(metadata, CatalogDateRole.Created)
                || HasRole(metadata, FieldSemanticRoleVocabulary.MetadataCreated),
            ProjectionMetadataSemantic.ModifiedDate => HasDate(metadata, CatalogDateRole.Modified)
                || HasRole(metadata, FieldSemanticRoleVocabulary.MetadataModified),
            ProjectionMetadataSemantic.SpatialExtent => metadata.Extents?.Spatial is not null
                || HasRole(metadata, FieldSemanticRoleVocabulary.GeometryPrimary),
            ProjectionMetadataSemantic.TemporalExtent => HasTemporalExtent(metadata),
            ProjectionMetadataSemantic.Lineage => HasText(metadata.Lineage?.Statement),
            ProjectionMetadataSemantic.Quality => metadata.Quality.Any()
                || HasRole(metadata, FieldSemanticRoleVocabulary.QualityFlag),
            ProjectionMetadataSemantic.Link => metadata.Links.Any(link => link.Href.IsAbsoluteUri),
            ProjectionMetadataSemantic.Distribution => metadata.Distributions.Any(distribution => distribution.Href.IsAbsoluteUri),
            ProjectionMetadataSemantic.PrimaryGeometryField => HasRole(metadata, FieldSemanticRoleVocabulary.GeometryPrimary),
            ProjectionMetadataSemantic.TemporalField => HasAnyRole(
                metadata,
                FieldSemanticRoleVocabulary.TemporalInstant,
                FieldSemanticRoleVocabulary.TemporalStart,
                FieldSemanticRoleVocabulary.TemporalEnd),
            ProjectionMetadataSemantic.AssetHrefField => HasRole(metadata, FieldSemanticRoleVocabulary.AssetHref)
                || metadata.Distributions.Any(distribution => distribution.Href.IsAbsoluteUri),
            ProjectionMetadataSemantic.LicenseCodeField => HasRole(metadata, FieldSemanticRoleVocabulary.LicenseCode),
            ProjectionMetadataSemantic.QualityFlagField => HasRole(metadata, FieldSemanticRoleVocabulary.QualityFlag),
            ProjectionMetadataSemantic.LifecycleStatusField => HasRole(metadata, FieldSemanticRoleVocabulary.StatusLifecycle),
            _ => false
        };

    private static bool HasPrimaryIdentifier(CatalogMetadataSemantics metadata) =>
        metadata.Identifiers.Any(identifier => identifier.IsPrimary && HasText(identifier.Value))
        || HasRole(metadata, FieldSemanticRoleVocabulary.IdentifierPrimary);

    private static bool HasLicense(CatalogMetadataSemantics metadata) =>
        HasText(metadata.Rights?.LicenseCode)
        || HasText(metadata.Rights?.LicenseTitle)
        || metadata.Rights?.LicenseUri is not null
        || HasRole(metadata, FieldSemanticRoleVocabulary.LicenseCode);

    private static bool HasTemporalExtent(CatalogMetadataSemantics metadata) =>
        metadata.Extents?.Temporal is { } temporal
            && (temporal.Instant.HasValue || temporal.Start.HasValue || temporal.End.HasValue)
        || HasDate(metadata, CatalogDateRole.TemporalInstant)
        || HasDate(metadata, CatalogDateRole.TemporalStart)
        || HasDate(metadata, CatalogDateRole.TemporalEnd)
        || HasAnyRole(
            metadata,
            FieldSemanticRoleVocabulary.TemporalInstant,
            FieldSemanticRoleVocabulary.TemporalStart,
            FieldSemanticRoleVocabulary.TemporalEnd);

    private static bool HasDate(CatalogMetadataSemantics metadata, CatalogDateRole role) =>
        metadata.Dates.Any(date => date.Role == role);

    private static bool HasRole(CatalogMetadataSemantics metadata, string role) =>
        metadata.Fields.Any(field => string.Equals(field.Role.Value, role, StringComparison.Ordinal));

    private static bool HasAnyRole(CatalogMetadataSemantics metadata, params string[] roles) =>
        metadata.Fields.Any(field => roles.Contains(field.Role.Value, StringComparer.Ordinal));

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// Readiness result for one metadata projection target.
/// </summary>
public sealed record ProjectionReadinessResult(
    MetadataProjectionTarget Target,
    string TargetLabel,
    string TargetSlug,
    bool IsReady,
    IReadOnlyList<ProjectionRequirement> Satisfied,
    IReadOnlyList<ProjectionRequirement> MissingRequired,
    IReadOnlyList<ProjectionRequirement> MissingRecommended);
