// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Pure in-memory compatibility analyzer for Metadata v2 release packages.
/// </summary>
public static class MetadataCompatibilityAnalyzer
{
    private const string DomainIdKey = "domainId";
    private const string DomainKey = "domain";
    private const string DomainsKey = "domains";
    private const string IndexNameKey = "indexName";
    private const string IndexKey = "index";
    private const string IndexesKey = "indexes";
    private const string IndexedKey = "indexed";

    /// <summary>
    /// Creates an unknown-state report for package or graph resolution failures.
    /// </summary>
    public static MetadataCompatibilityReport CreateUnavailableReport(
        MetadataReleasePackage? package,
        string targetEnvironment,
        DateTimeOffset generatedAt,
        string affectedSemanticId,
        string expected,
        string actual,
        string message,
        int scriptCount)
    {
        ArgumentNullException.ThrowIfNull(targetEnvironment);
        ArgumentNullException.ThrowIfNull(affectedSemanticId);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(message);

        var finding = new MetadataCompatibilityFinding
        {
            Code = MetadataCompatibilityCode.StateUnavailable,
            Severity = MetadataCompatibilityFindingSeverity.Error,
            Kind = MetadataCompatibilityFindingKind.State,
            AffectedSemanticId = affectedSemanticId,
            AffectedSemanticKind = MetadataSemanticArtifactKind.Resource,
            Message = message,
            Expected = Value("state", expected),
            Actual = Value("state", actual),
            RequiredAction = MetadataCompatibilityRequiredAction.ResolveState,
            CoverageState = MetadataCompatibilityCoverageState.Unknown,
        };

        return BuildReport(
            package,
            targetEnvironment.Trim(),
            targetRevision: null,
            targetEtag: null,
            generatedAt,
            [finding],
            Array.Empty<MetadataAffectedDependent>(),
            Array.Empty<MetadataDataScriptEntry>(),
            scriptCount);
    }

    /// <summary>
    /// Analyzes the release package against source and target graph snapshots.
    /// </summary>
    public static MetadataCompatibilityReport Analyze(
        MetadataReleasePackage package,
        MetadataV2GraphSnapshot sourceSnapshot,
        MetadataV2GraphSnapshot targetSnapshot,
        IReadOnlyList<MetadataDataScriptEntry> dataScripts,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        dataScripts ??= Array.Empty<MetadataDataScriptEntry>();

        var findings = new List<MetadataCompatibilityFinding>();
        var changedSemanticIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in package.Entries)
        {
            var sourceArtifact = ResolveArtifact(sourceSnapshot, entry);
            if (sourceArtifact is null)
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.StateUnavailable,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.State,
                    entry.SemanticId,
                    entry.ArtifactKind,
                    null,
                    null,
                    "The release package entry cannot be resolved in the source graph snapshot.",
                    Value("source", entry.SemanticId),
                    Value("source", "missing"),
                    MetadataCompatibilityRequiredAction.ResolveState,
                    MetadataCompatibilityCoverageState.Unknown));
                continue;
            }

            changedSemanticIds.Add(entry.SemanticId);
            if (sourceArtifact.ParentResource is not null)
            {
                changedSemanticIds.Add(sourceArtifact.ParentResource.Metadata.Id);
            }

            switch (sourceArtifact.Kind)
            {
                case MetadataSemanticArtifactKind.Resource:
                    CompareResource(sourceArtifact.Resource!, sourceSnapshot, targetSnapshot, findings);
                    break;
                case MetadataSemanticArtifactKind.Field:
                    CompareFieldEntry(sourceArtifact.ParentResource!, sourceArtifact.Field!, targetSnapshot, findings);
                    break;
                case MetadataSemanticArtifactKind.Service:
                    CompareService(sourceArtifact.Service!, targetSnapshot, findings);
                    break;
                case MetadataSemanticArtifactKind.Publication:
                    ComparePublication(sourceArtifact.Publication!, targetSnapshot, findings);
                    break;
                case MetadataSemanticArtifactKind.Catalog:
                    CompareCatalog(sourceArtifact.Catalog!, targetSnapshot, findings);
                    break;
                case MetadataSemanticArtifactKind.Policy:
                    ComparePolicy(sourceArtifact.Policy!, targetSnapshot, findings);
                    break;
                case MetadataSemanticArtifactKind.Role:
                    CompareRole(sourceArtifact.Role!, targetSnapshot, findings);
                    break;
            }
        }

        findings = ApplyDataScriptCoverage(findings, targetSnapshot, dataScripts, package.TargetEnvironments, targetSnapshot.Graph.Environment);
        var dependents = BuildAffectedDependents(package, targetSnapshot, changedSemanticIds, findings);
        return BuildReport(
            package,
            targetSnapshot.Graph.Environment,
            targetSnapshot.Revision,
            targetSnapshot.Etag,
            generatedAt,
            findings,
            dependents,
            dataScripts,
            dataScripts.Count);
    }

    private static void CompareResource(
        MetadataV2Resource source,
        MetadataV2GraphSnapshot sourceSnapshot,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (!targetSnapshot.Index.ResourcesById.TryGetValue(source.Metadata.Id, out var target))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.ResourceMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Resource,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target environment is missing a required resource.",
                Value("resourceType", source.Type.ToString()),
                Value("resource", "missing"),
                MetadataCompatibilityRequiredAction.AddResource,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        if (source.Type != target.Type)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.ResourceTypeMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Resource,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target resource type does not match the proposed resource type.",
                Value("resourceType", source.Type.ToString()),
                Value("resourceType", target.Type.ToString()),
                MetadataCompatibilityRequiredAction.UpdateResource,
                MetadataCompatibilityCoverageState.Uncovered));
        }

        CompareResourceFields(source, target, findings);
        CompareIdentifiers(source, target, findings);
        CompareSpatial(source, target, findings);
        CompareTemporal(source, target, findings);
        CompareStorage(source, target, sourceSnapshot, targetSnapshot, findings);
        ComparePolicyReferences(source.Metadata.Id, MetadataSemanticArtifactKind.Resource, source.PolicyIds, target.PolicyIds, targetSnapshot, findings);
    }

    private static void CompareResourceFields(
        MetadataV2Resource source,
        MetadataV2Resource target,
        List<MetadataCompatibilityFinding> findings)
    {
        foreach (var sourceField in source.SchemaFields)
        {
            CompareField(source, sourceField, target, findings);
        }
    }

    private static void CompareFieldEntry(
        MetadataV2Resource sourceParent,
        MetadataV2Field sourceField,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (!targetSnapshot.Index.ResourcesById.TryGetValue(sourceParent.Metadata.Id, out var targetParent))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.ResourceMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Resource,
                sourceParent.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                sourceParent.Metadata.Title ?? sourceParent.Metadata.Name,
                "The target environment is missing the parent resource for a required field.",
                Value("resourceType", sourceParent.Type.ToString()),
                Value("resource", "missing"),
                MetadataCompatibilityRequiredAction.AddResource,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        CompareField(sourceParent, sourceField, targetParent, findings);
    }

    private static void CompareField(
        MetadataV2Resource sourceResource,
        MetadataV2Field sourceField,
        MetadataV2Resource targetResource,
        List<MetadataCompatibilityFinding> findings)
    {
        var fieldSemanticId = GetFieldSemanticId(sourceResource, sourceField);
        var targetField = FindMatchingField(targetResource, sourceField);
        var expectedDetails = FieldDetails(sourceResource, sourceField);

        if (targetField is null)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.FieldMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Field,
                fieldSemanticId,
                MetadataSemanticArtifactKind.Field,
                sourceResource.Metadata.Id,
                sourceField.Title ?? sourceField.Name,
                "The target resource is missing a required field.",
                Value("field", sourceField.Name, expectedDetails),
                Value("field", "missing"),
                MetadataCompatibilityRequiredAction.AddField,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        if (!string.Equals(sourceField.Type, targetField.Type, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.FieldTypeMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Field,
                fieldSemanticId,
                MetadataSemanticArtifactKind.Field,
                sourceResource.Metadata.Id,
                sourceField.Title ?? sourceField.Name,
                "The target field type does not match the proposed field type.",
                Value("fieldType", sourceField.Type, expectedDetails),
                Value("fieldType", targetField.Type, FieldDetails(targetResource, targetField)),
                MetadataCompatibilityRequiredAction.UpdateField,
                MetadataCompatibilityCoverageState.Uncovered));
        }

        if (sourceField.Nullable != targetField.Nullable)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.FieldNullabilityMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Field,
                fieldSemanticId,
                MetadataSemanticArtifactKind.Field,
                sourceResource.Metadata.Id,
                sourceField.Title ?? sourceField.Name,
                "The target field nullability does not match the proposed field nullability.",
                Value("nullable", sourceField.Nullable.ToString(CultureInfo.InvariantCulture), expectedDetails),
                Value("nullable", targetField.Nullable.ToString(CultureInfo.InvariantCulture), FieldDetails(targetResource, targetField)),
                MetadataCompatibilityRequiredAction.UpdateField,
                MetadataCompatibilityCoverageState.Uncovered));
        }

        CompareCanonicalFieldTokens(
            sourceResource,
            sourceField,
            targetResource,
            targetField,
            ExtractTokens(sourceField.Extensions, DomainIdKey, DomainKey, DomainsKey),
            ExtractTokens(targetField.Extensions, DomainIdKey, DomainKey, DomainsKey),
            MetadataCompatibilityCode.FieldDomainMismatch,
            "domain",
            findings);

        CompareCanonicalFieldTokens(
            sourceResource,
            sourceField,
            targetResource,
            targetField,
            ExtractTokens(sourceField.Extensions, IndexNameKey, IndexKey, IndexesKey, IndexedKey),
            ExtractTokens(targetField.Extensions, IndexNameKey, IndexKey, IndexesKey, IndexedKey),
            MetadataCompatibilityCode.FieldIndexMissing,
            "index",
            findings);
    }

    private static void CompareCanonicalFieldTokens(
        MetadataV2Resource sourceResource,
        MetadataV2Field sourceField,
        MetadataV2Resource targetResource,
        MetadataV2Field targetField,
        string[] expectedTokens,
        string[] actualTokens,
        string code,
        string label,
        List<MetadataCompatibilityFinding> findings)
    {
        if (expectedTokens.Length == 0)
        {
            return;
        }

        var missing = expectedTokens
            .Where(token => !actualTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        findings.Add(Finding(
            code,
            MetadataCompatibilityFindingSeverity.Warning,
            MetadataCompatibilityFindingKind.Field,
            GetFieldSemanticId(sourceResource, sourceField),
            MetadataSemanticArtifactKind.Field,
            sourceResource.Metadata.Id,
            sourceField.Title ?? sourceField.Name,
            $"The target field is missing required canonical {label} metadata.",
            Value(label, string.Join(",", expectedTokens), FieldDetails(sourceResource, sourceField)),
            Value(label, actualTokens.Length == 0 ? "unavailable" : string.Join(",", actualTokens), FieldDetails(targetResource, targetField)),
            MetadataCompatibilityRequiredAction.UpdateField,
            actualTokens.Length == 0 ? MetadataCompatibilityCoverageState.Unknown : MetadataCompatibilityCoverageState.NotApplicable));
    }

    private static void CompareIdentifiers(
        MetadataV2Resource source,
        MetadataV2Resource target,
        List<MetadataCompatibilityFinding> findings)
    {
        var sourceIdentifier = FindPrimaryIdentifierField(source);
        if (sourceIdentifier is null)
        {
            return;
        }

        var targetIdentifier = FindPrimaryIdentifierField(target);
        if (targetIdentifier is not null &&
            (string.Equals(sourceIdentifier.Name, targetIdentifier.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(GetFieldSemanticId(source, sourceIdentifier), GetFieldSemanticId(target, targetIdentifier), StringComparison.Ordinal)))
        {
            return;
        }

        findings.Add(Finding(
            MetadataCompatibilityCode.IdentifierMissing,
            MetadataCompatibilityFindingSeverity.Error,
            MetadataCompatibilityFindingKind.Identifier,
            GetFieldSemanticId(source, sourceIdentifier),
            MetadataSemanticArtifactKind.Field,
            source.Metadata.Id,
            sourceIdentifier.Title ?? sourceIdentifier.Name,
            "The target resource is missing the required primary identifier field.",
            Value("identifier", sourceIdentifier.Name, FieldDetails(source, sourceIdentifier)),
            Value("identifier", targetIdentifier?.Name ?? "missing"),
            MetadataCompatibilityRequiredAction.UpdateIdentifier,
            MetadataCompatibilityCoverageState.Uncovered));
    }

    private static void CompareSpatial(
        MetadataV2Resource source,
        MetadataV2Resource target,
        List<MetadataCompatibilityFinding> findings)
    {
        var expectedGeometryType = source.ReadGeometryType();
        if (!string.IsNullOrWhiteSpace(expectedGeometryType))
        {
            var actualGeometryType = target.ReadGeometryType();
            if (string.IsNullOrWhiteSpace(actualGeometryType))
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.SpatialGeometryTypeMismatch,
                    MetadataCompatibilityFindingSeverity.Warning,
                    MetadataCompatibilityFindingKind.Spatial,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Resource,
                    null,
                    source.Metadata.Title ?? source.Metadata.Name,
                    "The target resource does not declare comparable geometry type metadata.",
                    Value("geometryType", expectedGeometryType),
                    Value("geometryType", "unavailable"),
                    MetadataCompatibilityRequiredAction.UpdateSpatial,
                    MetadataCompatibilityCoverageState.Unknown));
            }
            else if (!string.Equals(expectedGeometryType, actualGeometryType, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.SpatialGeometryTypeMismatch,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.Spatial,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Resource,
                    null,
                    source.Metadata.Title ?? source.Metadata.Name,
                    "The target resource geometry type does not match the proposed geometry type.",
                    Value("geometryType", expectedGeometryType),
                    Value("geometryType", actualGeometryType),
                    MetadataCompatibilityRequiredAction.UpdateSpatial,
                    MetadataCompatibilityCoverageState.Uncovered));
            }
        }

        var expectedSrid = source.ReadSrid();
        if (expectedSrid is not null)
        {
            var actualSrid = target.ReadSrid();
            if (actualSrid is null)
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.SpatialCrsMismatch,
                    MetadataCompatibilityFindingSeverity.Warning,
                    MetadataCompatibilityFindingKind.Spatial,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Resource,
                    null,
                    source.Metadata.Title ?? source.Metadata.Name,
                    "The target resource does not declare comparable CRS metadata.",
                    Value("srid", expectedSrid.Value.ToString(CultureInfo.InvariantCulture)),
                    Value("srid", "unavailable"),
                    MetadataCompatibilityRequiredAction.UpdateSpatial,
                    MetadataCompatibilityCoverageState.Unknown));
            }
            else if (expectedSrid.Value != actualSrid.Value)
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.SpatialCrsMismatch,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.Spatial,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Resource,
                    null,
                    source.Metadata.Title ?? source.Metadata.Name,
                    "The target resource CRS/SRID does not match the proposed CRS/SRID.",
                    Value("srid", expectedSrid.Value.ToString(CultureInfo.InvariantCulture)),
                    Value("srid", actualSrid.Value.ToString(CultureInfo.InvariantCulture)),
                    MetadataCompatibilityRequiredAction.UpdateSpatial,
                    MetadataCompatibilityCoverageState.Uncovered));
            }
        }
    }

    private static void CompareTemporal(
        MetadataV2Resource source,
        MetadataV2Resource target,
        List<MetadataCompatibilityFinding> findings)
    {
        var expected = source.ReadTemporalFields();
        if (expected.StartTimeField is null && expected.EndTimeField is null && expected.TrackIdField is null)
        {
            return;
        }

        var actual = target.ReadTemporalFields();
        if (!string.Equals(expected.StartTimeField, actual.StartTimeField, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.EndTimeField, actual.EndTimeField, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.TrackIdField, actual.TrackIdField, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.TemporalFieldMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Temporal,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target resource temporal field metadata does not match the proposed temporal field metadata.",
                Value("temporal", FormatTemporal(expected), TemporalDetails(expected)),
                Value("temporal", FormatTemporal(actual), TemporalDetails(actual)),
                MetadataCompatibilityRequiredAction.UpdateTemporal,
                MetadataCompatibilityCoverageState.Uncovered));
        }
    }

    private static void CompareStorage(
        MetadataV2Resource source,
        MetadataV2Resource target,
        MetadataV2GraphSnapshot sourceSnapshot,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        var sourceBinding = ResolvePrimaryStorage(source, sourceSnapshot);
        if (sourceBinding is null)
        {
            return;
        }

        var targetBinding = ResolvePrimaryStorage(target, targetSnapshot);
        if (targetBinding is null)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.StorageBindingMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Storage,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target resource is missing a required primary storage binding.",
                Value(
                    "storageBinding",
                    source.PrimaryStorageBindingId ?? sourceBinding.Metadata.Id,
                    StorageDetails(sourceBinding)),
                Value("storageBinding", "missing"),
                MetadataCompatibilityRequiredAction.UpdateStorage,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        if (sourceBinding.StorageType != targetBinding.StorageType)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.StorageTypeMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Storage,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target storage type does not match the proposed storage type.",
                Value("storageType", sourceBinding.StorageType.ToString()),
                Value("storageType", targetBinding.StorageType.ToString()),
                MetadataCompatibilityRequiredAction.UpdateStorage,
                MetadataCompatibilityCoverageState.Uncovered));
        }

        foreach (var capability in sourceBinding.Capabilities)
        {
            if (!targetBinding.Capabilities.Contains(capability))
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.StorageCapabilityMissing,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.Storage,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Resource,
                    null,
                    source.Metadata.Title ?? source.Metadata.Name,
                    "The target storage binding is missing a required capability.",
                    Value("capability", capability.ToString()),
                    Value("capability", "missing"),
                    MetadataCompatibilityRequiredAction.UpdateStorage,
                    MetadataCompatibilityCoverageState.Uncovered));
            }
        }

        if (!string.Equals(sourceBinding.Locator, targetBinding.Locator, StringComparison.Ordinal))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.StorageLocatorChanged,
                MetadataCompatibilityFindingSeverity.Warning,
                MetadataCompatibilityFindingKind.Storage,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Resource,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target storage locator differs from the proposed storage locator.",
                Value("locator", SafeLocator(sourceBinding.Locator)),
                Value("locator", SafeLocator(targetBinding.Locator)),
                MetadataCompatibilityRequiredAction.ManualReview,
                MetadataCompatibilityCoverageState.NotApplicable));
        }
    }

    private static void CompareService(
        MetadataV2Service source,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (!targetSnapshot.Index.ServicesById.TryGetValue(source.Metadata.Id, out var target))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.ServiceMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Service,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Service,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target environment is missing a required service.",
                Value("serviceType", source.ServiceType.ToString(), ServiceDetails(source)),
                Value("service", "missing"),
                MetadataCompatibilityRequiredAction.AddService,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        if (source.ServiceType != target.ServiceType)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.ServiceTypeMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Service,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Service,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target service type does not match the proposed service type.",
                Value("serviceType", source.ServiceType.ToString()),
                Value("serviceType", target.ServiceType.ToString()),
                MetadataCompatibilityRequiredAction.UpdateService,
                MetadataCompatibilityCoverageState.Uncovered));
        }

        if (!string.Equals(source.Route, target.Route, StringComparison.Ordinal))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.ServiceRouteChanged,
                MetadataCompatibilityFindingSeverity.Warning,
                MetadataCompatibilityFindingKind.Service,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Service,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target service route differs from the proposed service route.",
                Value("route", source.Route),
                Value("route", target.Route),
                MetadataCompatibilityRequiredAction.UpdateService,
                MetadataCompatibilityCoverageState.NotApplicable));
        }

        foreach (var protocol in source.EnabledProtocols ?? Array.Empty<string>())
        {
            if (target.EnabledProtocols is null ||
                !target.EnabledProtocols.Contains(protocol, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.ServiceProtocolMissing,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.Service,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Service,
                    null,
                    source.Metadata.Title ?? source.Metadata.Name,
                    "The target service is missing a required enabled protocol.",
                    Value("protocol", protocol),
                    Value("protocol", "missing"),
                    MetadataCompatibilityRequiredAction.UpdateService,
                    MetadataCompatibilityCoverageState.Uncovered));
            }
        }
    }

    private static void ComparePublication(
        MetadataV2Publication source,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (!targetSnapshot.Index.PublicationsById.TryGetValue(source.Metadata.Id, out var target))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.PublicationMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Publication,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Publication,
                source.ResourceId,
                source.TitleOverride ?? source.Metadata.Title ?? source.Metadata.Name,
                "The target environment is missing a required publication.",
                Value("publicationType", source.PublicationType.ToString(), PublicationDetails(source)),
                Value("publication", "missing"),
                MetadataCompatibilityRequiredAction.AddPublication,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        if (source.PublicationType != target.PublicationType)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.PublicationTypeMismatch,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Publication,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Publication,
                source.ResourceId,
                source.TitleOverride ?? source.Metadata.Title ?? source.Metadata.Name,
                "The target publication type does not match the proposed publication type.",
                Value("publicationType", source.PublicationType.ToString(), PublicationDetails(source)),
                Value("publicationType", target.PublicationType.ToString(), PublicationDetails(target)),
                MetadataCompatibilityRequiredAction.UpdatePublication,
                MetadataCompatibilityCoverageState.Uncovered));
        }

        if (!string.Equals(source.ResourceId, target.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(source.ServiceId, target.ServiceId, StringComparison.Ordinal) ||
            !string.Equals(source.Path, target.Path, StringComparison.Ordinal) ||
            !string.Equals(source.ServiceLocalId, target.ServiceLocalId, StringComparison.Ordinal) ||
            source.LayerIndex != target.LayerIndex)
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.PublicationRouteChanged,
                MetadataCompatibilityFindingSeverity.Warning,
                MetadataCompatibilityFindingKind.Publication,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Publication,
                source.ResourceId,
                source.TitleOverride ?? source.Metadata.Title ?? source.Metadata.Name,
                "The target publication resource, service, route, path, layer index, or local id differs from the proposed publication.",
                Value("publicationRoute", FormatPublicationRoute(source), PublicationDetails(source)),
                Value("publicationRoute", FormatPublicationRoute(target), PublicationDetails(target)),
                MetadataCompatibilityRequiredAction.UpdatePublication,
                MetadataCompatibilityCoverageState.NotApplicable));
        }

        foreach (var format in source.SupportedFormats)
        {
            if (!target.SupportedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.PublicationFormatMissing,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.Publication,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Publication,
                    source.ResourceId,
                    source.TitleOverride ?? source.Metadata.Title ?? source.Metadata.Name,
                    "The target publication is missing a required supported format.",
                    Value("format", format),
                    Value("format", "missing"),
                    MetadataCompatibilityRequiredAction.UpdatePublication,
                    MetadataCompatibilityCoverageState.Uncovered));
            }
        }

        foreach (var capability in source.Capabilities)
        {
            if (!target.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    MetadataCompatibilityCode.PublicationCapabilityMissing,
                    MetadataCompatibilityFindingSeverity.Error,
                    MetadataCompatibilityFindingKind.Publication,
                    source.Metadata.Id,
                    MetadataSemanticArtifactKind.Publication,
                    source.ResourceId,
                    source.TitleOverride ?? source.Metadata.Title ?? source.Metadata.Name,
                    "The target publication is missing a required capability.",
                    Value("capability", capability),
                    Value("capability", "missing"),
                    MetadataCompatibilityRequiredAction.UpdatePublication,
                    MetadataCompatibilityCoverageState.Uncovered));
            }
        }
    }

    private static void CompareCatalog(
        MetadataV2Catalog source,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (targetSnapshot.Index.CatalogsById.ContainsKey(source.Metadata.Id))
        {
            return;
        }

        findings.Add(Finding(
            MetadataCompatibilityCode.ResourceMissing,
            MetadataCompatibilityFindingSeverity.Error,
            MetadataCompatibilityFindingKind.Resource,
            source.Metadata.Id,
            MetadataSemanticArtifactKind.Catalog,
            null,
            source.Metadata.Title ?? source.Metadata.Name,
            "The target environment is missing a required catalog.",
            Value("catalog", source.Target),
            Value("catalog", "missing"),
            MetadataCompatibilityRequiredAction.AddResource,
            MetadataCompatibilityCoverageState.Uncovered));
    }

    private static void ComparePolicy(
        MetadataV2Policy source,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (targetSnapshot.Index.PoliciesById.ContainsKey(source.Metadata.Id))
        {
            return;
        }

        findings.Add(Finding(
            MetadataCompatibilityCode.PolicyReferenceMissing,
            MetadataCompatibilityFindingSeverity.Error,
            MetadataCompatibilityFindingKind.Policy,
            source.Metadata.Id,
            MetadataSemanticArtifactKind.Policy,
            null,
            source.Metadata.Title ?? source.Metadata.Name,
            "The target environment is missing a required policy.",
            Value("policy", source.Metadata.Id),
            Value("policy", "missing"),
            MetadataCompatibilityRequiredAction.ManualReview,
            MetadataCompatibilityCoverageState.Uncovered));
    }

    private static void CompareRole(
        MetadataV2Role source,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        if (!targetSnapshot.Index.RolesById.TryGetValue(source.Metadata.Id, out var target))
        {
            findings.Add(Finding(
                MetadataCompatibilityCode.PolicyReferenceMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Policy,
                source.Metadata.Id,
                MetadataSemanticArtifactKind.Role,
                null,
                source.Metadata.Title ?? source.Metadata.Name,
                "The target environment is missing a required role.",
                Value("role", source.Metadata.Id),
                Value("role", "missing"),
                MetadataCompatibilityRequiredAction.ManualReview,
                MetadataCompatibilityCoverageState.Uncovered));
            return;
        }

        ComparePolicyReferences(source.Metadata.Id, MetadataSemanticArtifactKind.Role, source.PolicyIds, target.PolicyIds, targetSnapshot, findings);
    }

    private static void ComparePolicyReferences(
        string semanticId,
        MetadataSemanticArtifactKind semanticKind,
        IReadOnlyList<string> expectedPolicyIds,
        IReadOnlyList<string> actualPolicyIds,
        MetadataV2GraphSnapshot targetSnapshot,
        List<MetadataCompatibilityFinding> findings)
    {
        foreach (var policyId in expectedPolicyIds)
        {
            if (actualPolicyIds.Contains(policyId, StringComparer.Ordinal) &&
                targetSnapshot.Index.PoliciesById.ContainsKey(policyId))
            {
                continue;
            }

            findings.Add(Finding(
                MetadataCompatibilityCode.PolicyReferenceMissing,
                MetadataCompatibilityFindingSeverity.Error,
                MetadataCompatibilityFindingKind.Policy,
                semanticId,
                semanticKind,
                null,
                semanticId,
                "The target environment is missing a required policy reference.",
                Value("policy", policyId),
                Value("policy", "missing"),
                MetadataCompatibilityRequiredAction.ManualReview,
                MetadataCompatibilityCoverageState.Uncovered));
        }
    }

    private static List<MetadataCompatibilityFinding> ApplyDataScriptCoverage(
        List<MetadataCompatibilityFinding> findings,
        MetadataV2GraphSnapshot targetSnapshot,
        IReadOnlyList<MetadataDataScriptEntry> dataScripts,
        IReadOnlyList<string> packageTargets,
        string targetEnvironment)
    {
        if (dataScripts.Count == 0)
        {
            return findings;
        }

        var updated = new List<MetadataCompatibilityFinding>(findings.Count);
        var scriptMismatchFindings = new List<MetadataCompatibilityFinding>();

        foreach (var finding in findings)
        {
            if (!IsCoverable(finding))
            {
                updated.Add(finding);
                continue;
            }

            var covered = false;
            var candidateMismatchFindings = new List<MetadataCompatibilityFinding>();
            var candidateMismatchKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var script in dataScripts)
            {
                if (!AppliesToTarget(script, targetEnvironment) ||
                    !AfterContractCovers(script.AfterContract, finding))
                {
                    continue;
                }

                if (BeforeContractMatches(script.BeforeContract, finding, targetSnapshot))
                {
                    updated.Add(finding with
                    {
                        CoverageState = MetadataCompatibilityCoverageState.CoveredByScript,
                        CoveringScriptId = script.ScriptId,
                        CoverageNotes = "Declared after-contract satisfies the requirement and before-contract matches target state.",
                        RequiredAction = MetadataCompatibilityRequiredAction.RunDataScript,
                    });
                    covered = true;
                    break;
                }

                var key = string.Concat(script.ScriptId, "|", finding.Code, "|", finding.AffectedSemanticId);
                if (candidateMismatchKeys.Add(key))
                {
                    candidateMismatchFindings.Add(Finding(
                        MetadataCompatibilityCode.ScriptBeforeContractMismatch,
                        MetadataCompatibilityFindingSeverity.Error,
                        MetadataCompatibilityFindingKind.Script,
                        finding.AffectedSemanticId,
                        finding.AffectedSemanticKind,
                        finding.AffectedParentSemanticId,
                        finding.DisplayName,
                        "A data script declares an after-contract that could cover this finding, but its before-contract does not match the target state.",
                        Value("scriptId", script.ScriptId),
                        Value("beforeContract", "mismatch"),
                        MetadataCompatibilityRequiredAction.ManualReview,
                        MetadataCompatibilityCoverageState.Uncovered));
                }
            }

            if (!covered)
            {
                updated.Add(finding);
                scriptMismatchFindings.AddRange(candidateMismatchFindings);
            }
        }

        updated.AddRange(scriptMismatchFindings);
        return updated
            .OrderBy(static finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(static finding => finding.AffectedSemanticId, StringComparer.Ordinal)
            .ThenBy(static finding => finding.AffectedParentSemanticId, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCoverable(MetadataCompatibilityFinding finding)
        => finding.Severity == MetadataCompatibilityFindingSeverity.Error &&
            finding.CoverageState == MetadataCompatibilityCoverageState.Uncovered;

    private static bool AppliesToTarget(MetadataDataScriptEntry script, string targetEnvironment)
        => string.IsNullOrWhiteSpace(script.TargetEnvironment) ||
            string.Equals(script.TargetEnvironment.Trim(), targetEnvironment, StringComparison.OrdinalIgnoreCase);

    private static bool AfterContractCovers(MetadataDataScriptContract? contract, MetadataCompatibilityFinding finding)
    {
        if (contract is null)
        {
            return false;
        }

        return finding.Code switch
        {
            MetadataCompatibilityCode.ResourceMissing => ResourceContractSatisfies(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.ResourceTypeMismatch => FindArtifactContract(contract, finding)?.ResourceType?.ToString() == finding.Expected.Value,
            MetadataCompatibilityCode.FieldMissing or
            MetadataCompatibilityCode.FieldTypeMismatch or
            MetadataCompatibilityCode.FieldNullabilityMismatch or
            MetadataCompatibilityCode.IdentifierMissing => FieldContractSatisfies(FindResourceContract(contract, finding), finding),
            MetadataCompatibilityCode.SpatialGeometryTypeMismatch or
            MetadataCompatibilityCode.SpatialCrsMismatch => SpatialContractSatisfies(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.TemporalFieldMismatch => TemporalContractSatisfies(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.StorageBindingMissing or
            MetadataCompatibilityCode.StorageTypeMismatch or
            MetadataCompatibilityCode.StorageCapabilityMissing => StorageContractSatisfies(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.ServiceMissing => ServiceContractSatisfies(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.ServiceTypeMismatch => FindArtifactContract(contract, finding)?.ServiceType?.ToString() == finding.Expected.Value,
            MetadataCompatibilityCode.ServiceProtocolMissing => FindArtifactContract(contract, finding)?.Capabilities.Contains(finding.Expected.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) == true,
            MetadataCompatibilityCode.PublicationMissing => PublicationContractSatisfies(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.PublicationTypeMismatch => FindArtifactContract(contract, finding)?.PublicationType?.ToString() == finding.Expected.Value,
            MetadataCompatibilityCode.PublicationFormatMissing => FindArtifactContract(contract, finding)?.SupportedFormats.Contains(finding.Expected.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) == true,
            MetadataCompatibilityCode.PublicationCapabilityMissing => FindArtifactContract(contract, finding)?.Capabilities.Contains(finding.Expected.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) == true,
            MetadataCompatibilityCode.PolicyReferenceMissing => FindArtifactContract(contract, finding)?.Exists == true,
            _ => false,
        };
    }

    private static bool BeforeContractMatches(
        MetadataDataScriptContract? contract,
        MetadataCompatibilityFinding finding,
        MetadataV2GraphSnapshot targetSnapshot)
    {
        if (contract is null)
        {
            return false;
        }

        return finding.Code switch
        {
            MetadataCompatibilityCode.ResourceMissing or
            MetadataCompatibilityCode.ServiceMissing or
            MetadataCompatibilityCode.PublicationMissing or
            MetadataCompatibilityCode.PolicyReferenceMissing =>
                FindArtifactContract(contract, finding)?.Exists == false && !TargetArtifactExists(targetSnapshot, finding),
            MetadataCompatibilityCode.ResourceTypeMismatch =>
                FindArtifactContract(contract, finding)?.ResourceType?.ToString() == finding.Actual.Value,
            MetadataCompatibilityCode.FieldMissing =>
                FindFieldContract(FindResourceContract(contract, finding), finding)?.Exists == false,
            MetadataCompatibilityCode.FieldTypeMismatch =>
                string.Equals(FindFieldContract(FindResourceContract(contract, finding), finding)?.Type, finding.Actual.Value, StringComparison.OrdinalIgnoreCase),
            MetadataCompatibilityCode.FieldNullabilityMismatch =>
                BoolToString(FindFieldContract(FindResourceContract(contract, finding), finding)?.Nullable) == finding.Actual.Value,
            MetadataCompatibilityCode.IdentifierMissing =>
                FindFieldContract(FindResourceContract(contract, finding), finding)?.Exists == false,
            MetadataCompatibilityCode.SpatialGeometryTypeMismatch or
            MetadataCompatibilityCode.SpatialCrsMismatch =>
                SpatialContractMatchesActual(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.TemporalFieldMismatch =>
                TemporalContractMatchesActual(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.StorageBindingMissing =>
                FindArtifactContract(contract, finding)?.Storage is null,
            MetadataCompatibilityCode.StorageTypeMismatch =>
                FindArtifactContract(contract, finding)?.Storage?.StorageType?.ToString() == finding.Actual.Value,
            MetadataCompatibilityCode.StorageCapabilityMissing =>
                StorageContractMatchesMissingCapability(FindArtifactContract(contract, finding), finding),
            MetadataCompatibilityCode.ServiceTypeMismatch =>
                FindArtifactContract(contract, finding)?.ServiceType?.ToString() == finding.Actual.Value,
            MetadataCompatibilityCode.ServiceProtocolMissing =>
                FindArtifactContract(contract, finding)?.Capabilities.Contains(finding.Expected.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) == false,
            MetadataCompatibilityCode.PublicationTypeMismatch =>
                FindArtifactContract(contract, finding)?.PublicationType?.ToString() == finding.Actual.Value,
            MetadataCompatibilityCode.PublicationFormatMissing =>
                FindArtifactContract(contract, finding)?.SupportedFormats.Contains(finding.Expected.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) == false,
            MetadataCompatibilityCode.PublicationCapabilityMissing =>
                FindArtifactContract(contract, finding)?.Capabilities.Contains(finding.Expected.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) == false,
            _ => false,
        };
    }

    private static bool StorageContractMatchesMissingCapability(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
        => !string.IsNullOrWhiteSpace(finding.Expected.Value) &&
            contract?.Storage is not null &&
            !contract.Storage.Capabilities.Any(capability =>
                string.Equals(capability.ToString(), finding.Expected.Value, StringComparison.Ordinal));

    private static bool TargetArtifactExists(MetadataV2GraphSnapshot targetSnapshot, MetadataCompatibilityFinding finding)
        => finding.AffectedSemanticKind switch
        {
            MetadataSemanticArtifactKind.Resource => targetSnapshot.Index.ResourcesById.ContainsKey(finding.AffectedSemanticId),
            MetadataSemanticArtifactKind.Service => targetSnapshot.Index.ServicesById.ContainsKey(finding.AffectedSemanticId),
            MetadataSemanticArtifactKind.Publication => targetSnapshot.Index.PublicationsById.ContainsKey(finding.AffectedSemanticId),
            MetadataSemanticArtifactKind.Catalog => targetSnapshot.Index.CatalogsById.ContainsKey(finding.AffectedSemanticId),
            MetadataSemanticArtifactKind.Policy => targetSnapshot.Index.PoliciesById.ContainsKey(finding.AffectedSemanticId),
            MetadataSemanticArtifactKind.Role => targetSnapshot.Index.RolesById.ContainsKey(finding.AffectedSemanticId),
            MetadataSemanticArtifactKind.Field => finding.AffectedParentSemanticId is { Length: > 0 } parentId &&
                targetSnapshot.Index.ResourcesById.TryGetValue(parentId, out var resource) &&
                resource.SchemaFields.Any(field => string.Equals(GetFieldSemanticId(resource, field), finding.AffectedSemanticId, StringComparison.Ordinal)),
            _ => false,
        };

    private static MetadataScriptResourceContract? FindArtifactContract(
        MetadataDataScriptContract contract,
        MetadataCompatibilityFinding finding)
        => contract.Resources.FirstOrDefault(resource =>
            string.Equals(resource.SemanticId, finding.AffectedSemanticId, StringComparison.Ordinal) &&
            resource.SemanticKind == finding.AffectedSemanticKind);

    private static MetadataScriptResourceContract? FindResourceContract(
        MetadataDataScriptContract contract,
        MetadataCompatibilityFinding finding)
    {
        var resourceId = finding.AffectedParentSemanticId ?? finding.AffectedSemanticId;
        return contract.Resources.FirstOrDefault(resource =>
            string.Equals(resource.SemanticId, resourceId, StringComparison.Ordinal) &&
            resource.SemanticKind == MetadataSemanticArtifactKind.Resource);
    }

    private static bool ResourceContractSatisfies(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
        => contract?.Exists == true &&
            contract.ResourceType?.ToString() == finding.Expected.Value;

    private static bool ServiceContractSatisfies(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
    {
        if (contract?.Exists != true ||
            contract.ServiceType?.ToString() != finding.Expected.Value)
        {
            return false;
        }

        if (!ContractDetailMatches(contract.Route, finding.Expected.Details, "route"))
        {
            return false;
        }

        return ContractContainsAll(
            contract.Capabilities,
            finding.Expected.Details,
            "enabledProtocols");
    }

    private static bool PublicationContractSatisfies(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
    {
        if (contract?.Exists != true ||
            contract.PublicationType?.ToString() != finding.Expected.Value)
        {
            return false;
        }

        return ContractDetailMatches(contract.ResourceId, finding.Expected.Details, "resourceId") &&
            ContractDetailMatches(contract.ServiceId, finding.Expected.Details, "serviceId") &&
            ContractDetailMatches(contract.Path, finding.Expected.Details, "path") &&
            ContractDetailMatches(contract.ServiceLocalId, finding.Expected.Details, "serviceLocalId") &&
            ContractDetailMatches(contract.LayerIndex?.ToString(CultureInfo.InvariantCulture), finding.Expected.Details, "layerIndex") &&
            ContractContainsAll(contract.SupportedFormats, finding.Expected.Details, "supportedFormats") &&
            ContractContainsAll(contract.Capabilities, finding.Expected.Details, "capabilities");
    }

    private static bool FieldContractSatisfies(
        MetadataScriptResourceContract? resourceContract,
        MetadataCompatibilityFinding finding)
    {
        var field = FindFieldContract(resourceContract, finding);
        if (field is null || field.Exists == false)
        {
            return false;
        }

        if (finding.Expected.Details.TryGetValue("type", out var type) &&
            !string.Equals(field.Type, type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (finding.Expected.Details.TryGetValue("nullable", out var nullable) &&
            BoolToString(field.Nullable) != nullable)
        {
            return false;
        }

        if (finding.Code == MetadataCompatibilityCode.IdentifierMissing)
        {
            return field.SemanticRoles.Contains("id.primary", StringComparer.OrdinalIgnoreCase) ||
                field.SemanticRoles.Contains("identifier.primary", StringComparer.OrdinalIgnoreCase);
        }

        return true;
    }

    private static MetadataScriptFieldContract? FindFieldContract(
        MetadataScriptResourceContract? resourceContract,
        MetadataCompatibilityFinding finding)
    {
        if (resourceContract is null)
        {
            return null;
        }

        finding.Expected.Details.TryGetValue("fieldName", out var fieldName);
        finding.Expected.Details.TryGetValue("semanticId", out var semanticId);
        return resourceContract.Fields.FirstOrDefault(field =>
            (!string.IsNullOrWhiteSpace(semanticId) &&
             string.Equals(field.SemanticId, semanticId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(fieldName) &&
             string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool SpatialContractSatisfies(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
    {
        if (contract?.Spatial is null)
        {
            return false;
        }

        return finding.Code switch
        {
            MetadataCompatibilityCode.SpatialGeometryTypeMismatch =>
                string.Equals(contract.Spatial.GeometryType, finding.Expected.Value, StringComparison.OrdinalIgnoreCase),
            MetadataCompatibilityCode.SpatialCrsMismatch =>
                contract.Spatial.Srid?.ToString(CultureInfo.InvariantCulture) == finding.Expected.Value,
            _ => false,
        };
    }

    private static bool SpatialContractMatchesActual(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
    {
        if (contract?.Spatial is null)
        {
            return false;
        }

        return finding.Code switch
        {
            MetadataCompatibilityCode.SpatialGeometryTypeMismatch =>
                string.Equals(contract.Spatial.GeometryType, finding.Actual.Value, StringComparison.OrdinalIgnoreCase),
            MetadataCompatibilityCode.SpatialCrsMismatch =>
                contract.Spatial.Srid?.ToString(CultureInfo.InvariantCulture) == finding.Actual.Value,
            _ => false,
        };
    }

    private static bool TemporalContractSatisfies(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
        => contract?.Temporal is not null &&
            finding.Expected.Details.TryGetValue("startTimeField", out var start) &&
            finding.Expected.Details.TryGetValue("endTimeField", out var end) &&
            finding.Expected.Details.TryGetValue("trackIdField", out var track) &&
            string.Equals(contract.Temporal.StartTimeField ?? string.Empty, start, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contract.Temporal.EndTimeField ?? string.Empty, end, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contract.Temporal.TrackIdField ?? string.Empty, track, StringComparison.OrdinalIgnoreCase);

    private static bool TemporalContractMatchesActual(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
        => contract?.Temporal is not null &&
            finding.Actual.Details.TryGetValue("startTimeField", out var start) &&
            finding.Actual.Details.TryGetValue("endTimeField", out var end) &&
            finding.Actual.Details.TryGetValue("trackIdField", out var track) &&
            string.Equals(contract.Temporal.StartTimeField ?? string.Empty, start, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contract.Temporal.EndTimeField ?? string.Empty, end, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contract.Temporal.TrackIdField ?? string.Empty, track, StringComparison.OrdinalIgnoreCase);

    private static bool StorageContractSatisfies(
        MetadataScriptResourceContract? contract,
        MetadataCompatibilityFinding finding)
    {
        if (contract?.Storage is null)
        {
            return false;
        }

        return finding.Code switch
        {
            MetadataCompatibilityCode.StorageBindingMissing =>
                ContractDetailMatches(contract.Storage.StorageBindingId, finding.Expected.Value) &&
                ContractDetailMatches(contract.Storage.StorageType?.ToString(), finding.Expected.Details, "storageType") &&
                ContractContainsAll(contract.Storage.Capabilities.Select(static capability => capability.ToString()), finding.Expected.Details, "capabilities"),
            MetadataCompatibilityCode.StorageTypeMismatch => contract.Storage.StorageType?.ToString() == finding.Expected.Value,
            MetadataCompatibilityCode.StorageCapabilityMissing => contract.Storage.Capabilities.Any(capability => capability.ToString() == finding.Expected.Value),
            _ => false,
        };
    }

    private static bool ContractDetailMatches(
        string? actual,
        IReadOnlyDictionary<string, string> expectedDetails,
        string key)
    {
        if (!expectedDetails.TryGetValue(key, out var expected))
        {
            return true;
        }

        return ContractDetailMatches(actual, expected);
    }

    private static bool ContractDetailMatches(string? actual, string? expected)
        => string.Equals(actual ?? string.Empty, expected ?? string.Empty, StringComparison.Ordinal);

    private static bool ContractContainsAll(
        IEnumerable<string> actualValues,
        IReadOnlyDictionary<string, string> expectedDetails,
        string key)
    {
        if (!expectedDetails.TryGetValue(key, out var expected) ||
            string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        var actual = new HashSet<string>(actualValues, StringComparer.OrdinalIgnoreCase);
        foreach (var expectedValue in expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!actual.Contains(expectedValue))
            {
                return false;
            }
        }

        return true;
    }

    private static MetadataAffectedDependent[] BuildAffectedDependents(
        MetadataReleasePackage package,
        MetadataV2GraphSnapshot targetSnapshot,
        HashSet<string> changedSemanticIds,
        IReadOnlyList<MetadataCompatibilityFinding> findings)
    {
        var states = new Dictionary<string, DependentState>(StringComparer.Ordinal);

        foreach (var entry in package.Entries)
        {
            foreach (var dependentId in entry.DependentSemanticIds)
            {
                AddDependent(states, targetSnapshot, dependentId, entry.SemanticId, "release-package-dependent", "Declared release package dependent.", SeverityFor(entry.SemanticId, findings));
            }
        }

        foreach (var resourceId in changedSemanticIds)
        {
            foreach (var publication in targetSnapshot.Index.PublicationsByResource[resourceId])
            {
                AddDependent(states, targetSnapshot, publication.Metadata.Id, resourceId, "publication-by-resource", "Publication exposes a changed resource.", SeverityFor(resourceId, findings));
                AddDependent(states, targetSnapshot, publication.ServiceId, publication.Metadata.Id, "service-by-publication", "Service exposes an affected publication.", SeverityFor(resourceId, findings));
            }

            foreach (var publication in targetSnapshot.Index.PublicationsByService[resourceId])
            {
                AddDependent(states, targetSnapshot, publication.Metadata.Id, resourceId, "publication-by-service", "Publication belongs to a changed service.", SeverityFor(resourceId, findings));
            }
        }

        foreach (var resource in targetSnapshot.Graph.Resources)
        {
            foreach (var relationship in resource.Relationships)
            {
                if (changedSemanticIds.Contains(relationship.RelatedResourceId))
                {
                    AddDependent(states, targetSnapshot, resource.Metadata.Id, relationship.RelatedResourceId, "resource-relationship", "Resource declares a relationship to a changed resource.", SeverityFor(relationship.RelatedResourceId, findings));
                }
            }

            foreach (var styleResourceId in resource.StyleResourceIds)
            {
                if (changedSemanticIds.Contains(styleResourceId))
                {
                    AddDependent(states, targetSnapshot, resource.Metadata.Id, styleResourceId, "style-resource", "Resource references a changed style resource.", SeverityFor(styleResourceId, findings));
                }
            }
        }

        foreach (var profile in targetSnapshot.Graph.ProjectionProfiles)
        {
            foreach (var required in profile.RequiredSemantics)
            {
                if (changedSemanticIds.Contains(required))
                {
                    AddDependent(states, targetSnapshot, profile.Metadata.Id, required, "projection-required-semantics", "Projection profile requires a changed semantic.", SeverityFor(required, findings));
                }
            }
        }

        foreach (var role in targetSnapshot.Graph.Roles)
        {
            foreach (var policyId in role.PolicyIds)
            {
                if (changedSemanticIds.Contains(policyId))
                {
                    AddDependent(states, targetSnapshot, role.Metadata.Id, policyId, "role-policy", "Role references a changed policy.", SeverityFor(policyId, findings));
                }
            }
        }

        return states.Values
            .OrderBy(static state => state.SemanticId, StringComparer.Ordinal)
            .ThenBy(static state => state.Reason, StringComparer.Ordinal)
            .Select(static state => state.ToDependent())
            .ToArray();
    }

    private static void AddDependent(
        Dictionary<string, DependentState> states,
        MetadataV2GraphSnapshot snapshot,
        string semanticId,
        string viaSemanticId,
        string relationshipKind,
        string reason,
        MetadataCompatibilityFindingSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(semanticId))
        {
            return;
        }

        var key = string.Concat(semanticId, "|", reason);
        if (states.TryGetValue(key, out var existing))
        {
            if (SeverityRank(severity) > SeverityRank(existing.ImpactSeverity))
            {
                states[key] = existing with { ImpactSeverity = severity };
            }
            return;
        }

        var artifact = ResolveArtifact(snapshot, semanticId);
        states[key] = new DependentState(
            semanticId,
            artifact?.Kind ?? MetadataSemanticArtifactKind.Resource,
            artifact?.DisplayName,
            severity,
            reason,
            viaSemanticId,
            relationshipKind);
    }

    private static MetadataCompatibilityFindingSeverity SeverityFor(
        string semanticId,
        IReadOnlyList<MetadataCompatibilityFinding> findings)
    {
        var severity = MetadataCompatibilityFindingSeverity.Info;
        foreach (var finding in findings)
        {
            if ((string.Equals(finding.AffectedSemanticId, semanticId, StringComparison.Ordinal) ||
                 string.Equals(finding.AffectedParentSemanticId, semanticId, StringComparison.Ordinal)) &&
                SeverityRank(finding.Severity) > SeverityRank(severity))
            {
                severity = finding.Severity;
            }
        }

        return severity;
    }

    private static MetadataCompatibilityReport BuildReport(
        MetadataReleasePackage? package,
        string targetEnvironment,
        long? targetRevision,
        string? targetEtag,
        DateTimeOffset generatedAt,
        IReadOnlyList<MetadataCompatibilityFinding> findings,
        IReadOnlyList<MetadataAffectedDependent> dependents,
        IReadOnlyList<MetadataDataScriptEntry> dataScripts,
        int scriptCount)
    {
        var uncoveredErrorCount = findings.Count(static finding =>
            finding.Severity == MetadataCompatibilityFindingSeverity.Error &&
            finding.CoverageState != MetadataCompatibilityCoverageState.CoveredByScript);
        var coveredErrorCount = findings.Count(static finding =>
            finding.Severity == MetadataCompatibilityFindingSeverity.Error &&
            finding.CoverageState == MetadataCompatibilityCoverageState.CoveredByScript);
        var warningCount = findings.Count(static finding => finding.Severity == MetadataCompatibilityFindingSeverity.Warning);
        var hasUnknown = findings.Any(static finding =>
            finding.Code == MetadataCompatibilityCode.StateUnavailable ||
            finding.CoverageState == MetadataCompatibilityCoverageState.Unknown);

        var status = hasUnknown
            ? MetadataCompatibilityStatus.Unknown
            : uncoveredErrorCount > 0
                ? MetadataCompatibilityStatus.Blocked
                : warningCount > 0 || coveredErrorCount > 0
                    ? MetadataCompatibilityStatus.Warning
                    : MetadataCompatibilityStatus.Ready;

        return new MetadataCompatibilityReport
        {
            TargetEnvironment = targetEnvironment,
            ReleasePackageId = package?.PackageId,
            PackageKey = package?.Metadata.Name,
            SourceEnvironment = package?.SourceEnvironment,
            SourceRevision = package?.SourceRevision,
            SourceEtag = package?.SourceEtag,
            TargetRevision = targetRevision,
            TargetEtag = targetEtag,
            GeneratedAt = generatedAt,
            Status = status,
            CanCreatePullRequest = status is not MetadataCompatibilityStatus.Blocked and not MetadataCompatibilityStatus.Unknown,
            CanPromote = status is not MetadataCompatibilityStatus.Blocked and not MetadataCompatibilityStatus.Unknown,
            UncoveredErrorCount = uncoveredErrorCount,
            CoveredErrorCount = coveredErrorCount,
            WarningCount = warningCount,
            ScriptCount = scriptCount,
            Findings = findings
                .OrderBy(static finding => finding.Code, StringComparer.Ordinal)
                .ThenBy(static finding => finding.AffectedParentSemanticId, StringComparer.Ordinal)
                .ThenBy(static finding => finding.AffectedSemanticId, StringComparer.Ordinal)
                .ToArray(),
            AffectedDependents = dependents,
            RollbackReadiness = ClassifyRollback(findings, dataScripts),
        };
    }

    private static MetadataRollbackReadiness ClassifyRollback(
        IReadOnlyList<MetadataCompatibilityFinding> findings,
        IReadOnlyList<MetadataDataScriptEntry> dataScripts)
    {
        if (findings.Any(static finding =>
            finding.Code == MetadataCompatibilityCode.StateUnavailable ||
            finding.Code == MetadataCompatibilityCode.ScriptBeforeContractMismatch ||
            finding.CoverageState == MetadataCompatibilityCoverageState.Unknown))
        {
            return Rollback(
                MetadataRollbackReadinessClassification.Manual,
                "Rollback readiness cannot be determined while state, metadata coverage, or script applicability is unknown.",
                requiresManualAction: true);
        }

        var uncoveredErrors = findings.Where(static finding =>
            finding.Severity == MetadataCompatibilityFindingSeverity.Error &&
            finding.CoverageState != MetadataCompatibilityCoverageState.CoveredByScript).ToArray();
        if (uncoveredErrors.Length > 0 && uncoveredErrors.Any(static finding => IsDataImpacting(finding.Code)))
        {
            return Rollback(
                MetadataRollbackReadinessClassification.Manual,
                "Uncovered data-impacting errors require manual rollback planning.",
                requiresManualAction: true);
        }

        var coveredScripts = findings
            .Where(static finding => finding.CoverageState == MetadataCompatibilityCoverageState.CoveredByScript)
            .Select(static finding => finding.CoveringScriptId)
            .Where(static scriptId => !string.IsNullOrWhiteSpace(scriptId))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        if (coveredScripts.Length > 0)
        {
            var reversibleScriptIds = dataScripts
                .Where(script => script.Reversible)
                .Select(script => script.ScriptId)
                .ToHashSet(StringComparer.Ordinal);
            if (coveredScripts.Any(scriptId => !reversibleScriptIds.Contains(scriptId)))
            {
                return Rollback(
                    MetadataRollbackReadinessClassification.SnapshotRequired,
                    "At least one covering data script is not declared reversible.",
                    requiresSnapshot: true,
                    scriptIds: coveredScripts);
            }

            return Rollback(
                MetadataRollbackReadinessClassification.ScriptReversible,
                "Data-impacting findings are covered by declared scripts and require script rollback review.",
                scriptIds: coveredScripts);
        }

        if (findings.Any(static finding =>
            finding.Code is MetadataCompatibilityCode.ServiceRouteChanged or
                MetadataCompatibilityCode.ServiceTypeMismatch or
                MetadataCompatibilityCode.PublicationRouteChanged or
                MetadataCompatibilityCode.PublicationTypeMismatch))
        {
            return Rollback(
                MetadataRollbackReadinessClassification.ServiceRevision,
                "Service or publication identity changed and should be rolled back through a service revision.");
        }

        return Rollback(
            MetadataRollbackReadinessClassification.MetadataOnly,
            "Only Metadata v2 graph/package state is required for rollback.");
    }

    private static MetadataRollbackReadiness Rollback(
        MetadataRollbackReadinessClassification classification,
        string reason,
        bool requiresSnapshot = false,
        bool requiresManualAction = false,
        IReadOnlyList<string>? scriptIds = null)
        => new()
        {
            Classification = classification,
            Reason = reason,
            RequiresSnapshot = requiresSnapshot,
            RequiresManualAction = requiresManualAction,
            ScriptIds = scriptIds ?? Array.Empty<string>(),
        };

    private static bool IsDataImpacting(string code)
        => code is MetadataCompatibilityCode.ResourceMissing or
            MetadataCompatibilityCode.ResourceTypeMismatch or
            MetadataCompatibilityCode.FieldMissing or
            MetadataCompatibilityCode.FieldTypeMismatch or
            MetadataCompatibilityCode.FieldNullabilityMismatch or
            MetadataCompatibilityCode.IdentifierMissing or
            MetadataCompatibilityCode.SpatialGeometryTypeMismatch or
            MetadataCompatibilityCode.SpatialCrsMismatch or
            MetadataCompatibilityCode.TemporalFieldMismatch or
            MetadataCompatibilityCode.StorageBindingMissing or
            MetadataCompatibilityCode.StorageTypeMismatch or
            MetadataCompatibilityCode.StorageCapabilityMissing;

    private static MetadataV2StorageBinding? ResolvePrimaryStorage(
        MetadataV2Resource resource,
        MetadataV2GraphSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(resource.PrimaryStorageBindingId) &&
            snapshot.Index.StorageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primary))
        {
            return primary;
        }

        return snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].FirstOrDefault();
    }

    private static ResolvedArtifact? ResolveArtifact(MetadataV2GraphSnapshot snapshot, MetadataReleaseEntry entry)
    {
        if (entry.ArtifactKind == MetadataSemanticArtifactKind.Field && entry.SourceField is not null)
        {
            if (!snapshot.Index.ResourcesById.TryGetValue(entry.SourceField.ParentResourceId, out var parent))
            {
                return null;
            }

            var field = parent.SchemaFields.FirstOrDefault(field =>
                string.Equals(GetFieldSemanticId(parent, field), entry.SemanticId, StringComparison.Ordinal) ||
                string.Equals(field.Name, entry.SourceField.FieldName, StringComparison.OrdinalIgnoreCase));
            return field is null ? null : ResolvedArtifact.ForField(entry.SemanticId, parent, field);
        }

        return ResolveArtifact(snapshot, entry.SemanticId);
    }

    private static ResolvedArtifact? ResolveArtifact(MetadataV2GraphSnapshot snapshot, string semanticId)
    {
        if (snapshot.Index.ResourcesById.TryGetValue(semanticId, out var resource))
        {
            return new ResolvedArtifact(semanticId, MetadataSemanticArtifactKind.Resource, resource.Metadata.Title ?? resource.Metadata.Name, Resource: resource);
        }

        if (snapshot.Index.ServicesById.TryGetValue(semanticId, out var service))
        {
            return new ResolvedArtifact(semanticId, MetadataSemanticArtifactKind.Service, service.Metadata.Title ?? service.Metadata.Name, Service: service);
        }

        if (snapshot.Index.PublicationsById.TryGetValue(semanticId, out var publication))
        {
            return new ResolvedArtifact(semanticId, MetadataSemanticArtifactKind.Publication, publication.TitleOverride ?? publication.Metadata.Title ?? publication.Metadata.Name, Publication: publication);
        }

        if (snapshot.Index.CatalogsById.TryGetValue(semanticId, out var catalog))
        {
            return new ResolvedArtifact(semanticId, MetadataSemanticArtifactKind.Catalog, catalog.Metadata.Title ?? catalog.Metadata.Name, Catalog: catalog);
        }

        if (snapshot.Index.PoliciesById.TryGetValue(semanticId, out var policy))
        {
            return new ResolvedArtifact(semanticId, MetadataSemanticArtifactKind.Policy, policy.Metadata.Title ?? policy.Metadata.Name, Policy: policy);
        }

        if (snapshot.Index.RolesById.TryGetValue(semanticId, out var role))
        {
            return new ResolvedArtifact(semanticId, MetadataSemanticArtifactKind.Role, role.Metadata.Title ?? role.Metadata.Name, Role: role);
        }

        foreach (var candidateResource in snapshot.Graph.Resources)
        {
            foreach (var field in candidateResource.SchemaFields)
            {
                var fieldSemanticId = GetFieldSemanticId(candidateResource, field);
                if (string.Equals(fieldSemanticId, semanticId, StringComparison.Ordinal))
                {
                    return ResolvedArtifact.ForField(semanticId, candidateResource, field);
                }
            }
        }

        return null;
    }

    private static MetadataV2Field? FindMatchingField(MetadataV2Resource resource, MetadataV2Field sourceField)
    {
        var sourceSemanticId = GetFieldSemanticId(resource, sourceField);
        return resource.SchemaFields.FirstOrDefault(field =>
            string.Equals(GetFieldSemanticId(resource, field), sourceSemanticId, StringComparison.Ordinal) ||
            string.Equals(field.Name, sourceField.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static MetadataV2Field? FindPrimaryIdentifierField(MetadataV2Resource resource)
        => resource.SchemaFields.FirstOrDefault(static field =>
                field.SemanticRoles.Contains("id.primary", StringComparer.OrdinalIgnoreCase) ||
                field.SemanticRoles.Contains("identifier.primary", StringComparer.OrdinalIgnoreCase))
            ?? resource.FindPrimaryIdField();

    private static string GetFieldSemanticId(MetadataV2Resource resource, MetadataV2Field field)
        => string.IsNullOrWhiteSpace(field.SemanticId)
            ? $"field.{resource.Metadata.Id}.{field.Name}"
            : field.SemanticId.Trim();

    private static Dictionary<string, string> FieldDetails(MetadataV2Resource resource, MetadataV2Field field)
        => new(StringComparer.Ordinal)
        {
            ["semanticId"] = GetFieldSemanticId(resource, field),
            ["fieldName"] = field.Name,
            ["type"] = field.Type,
            ["nullable"] = field.Nullable.ToString(CultureInfo.InvariantCulture),
            ["resourceId"] = resource.Metadata.Id,
        };

    private static Dictionary<string, string> TemporalDetails(MetadataV2TemporalFields fields)
        => new(StringComparer.Ordinal)
        {
            ["startTimeField"] = fields.StartTimeField ?? string.Empty,
            ["endTimeField"] = fields.EndTimeField ?? string.Empty,
            ["trackIdField"] = fields.TrackIdField ?? string.Empty,
        };

    private static Dictionary<string, string> StorageDetails(MetadataV2StorageBinding binding)
        => new(StringComparer.Ordinal)
        {
            ["storageBindingId"] = binding.Metadata.Id,
            ["storageType"] = binding.StorageType.ToString(),
            ["capabilities"] = JoinTokens(binding.Capabilities.Select(static capability => capability.ToString())),
        };

    private static Dictionary<string, string> ServiceDetails(MetadataV2Service service)
        => new(StringComparer.Ordinal)
        {
            ["route"] = service.Route ?? string.Empty,
            ["enabledProtocols"] = JoinTokens(service.EnabledProtocols ?? Array.Empty<string>()),
        };

    private static Dictionary<string, string> PublicationDetails(MetadataV2Publication publication)
        => new(StringComparer.Ordinal)
        {
            ["resourceId"] = publication.ResourceId,
            ["serviceId"] = publication.ServiceId,
            ["path"] = publication.Path ?? string.Empty,
            ["serviceLocalId"] = publication.ServiceLocalId ?? string.Empty,
            ["layerIndex"] = publication.LayerIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["supportedFormats"] = JoinTokens(publication.SupportedFormats),
            ["capabilities"] = JoinTokens(publication.Capabilities),
        };

    private static string JoinTokens(IEnumerable<string> values)
        => string.Join(
            ",",
            values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Order(StringComparer.OrdinalIgnoreCase));

    private static string[] ExtractTokens(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var element))
            {
                continue;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    AddToken(tokens, element.GetString());
                    break;
                case JsonValueKind.True:
                    tokens.Add(key == IndexedKey ? "indexed" : key);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            AddToken(tokens, item.GetString());
                        }
                    }
                    break;
            }
        }

        return tokens.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddToken(HashSet<string> tokens, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tokens.Add(value.Trim());
        }
    }

    private static MetadataCompatibilityFinding Finding(
        string code,
        MetadataCompatibilityFindingSeverity severity,
        MetadataCompatibilityFindingKind kind,
        string semanticId,
        MetadataSemanticArtifactKind semanticKind,
        string? parentSemanticId,
        string? displayName,
        string message,
        MetadataCompatibilityValue expected,
        MetadataCompatibilityValue actual,
        MetadataCompatibilityRequiredAction requiredAction,
        MetadataCompatibilityCoverageState coverageState)
        => new()
        {
            Code = code,
            Severity = severity,
            Kind = kind,
            AffectedSemanticId = semanticId,
            AffectedSemanticKind = semanticKind,
            AffectedParentSemanticId = parentSemanticId,
            DisplayName = displayName,
            Message = message,
            Expected = expected,
            Actual = actual,
            RequiredAction = requiredAction,
            CoverageState = coverageState,
        };

    private static MetadataCompatibilityValue Value(
        string label,
        string? value,
        IReadOnlyDictionary<string, string>? details = null)
        => new()
        {
            Label = label,
            Value = value,
            Details = details ?? new Dictionary<string, string>(),
        };

    private static string FormatTemporal(MetadataV2TemporalFields fields)
        => string.Join(
            ",",
            new[]
            {
                fields.StartTimeField,
                fields.EndTimeField,
                fields.TrackIdField,
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatPublicationRoute(MetadataV2Publication publication)
        => string.Concat(
            publication.ResourceId,
            "|",
            publication.ServiceId,
            "|",
            publication.Path ?? string.Empty,
            "|",
            publication.ServiceLocalId ?? string.Empty,
            "|",
            publication.LayerIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string SafeLocator(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return string.Empty;
        }

        var trimmed = locator.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith('/') ||
            trimmed.Contains('\\') ||
            trimmed.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("connectionstring", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted]";
        }

        return trimmed.Length <= 96 ? trimmed : string.Concat(trimmed.AsSpan(0, 93), "...");
    }

    private static string? BoolToString(bool? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static int SeverityRank(MetadataCompatibilityFindingSeverity severity)
        => severity switch
        {
            MetadataCompatibilityFindingSeverity.Error => 3,
            MetadataCompatibilityFindingSeverity.Warning => 2,
            _ => 1,
        };

    private sealed record ResolvedArtifact(
        string SemanticId,
        MetadataSemanticArtifactKind Kind,
        string? DisplayName,
        MetadataV2Resource? Resource = null,
        MetadataV2Resource? ParentResource = null,
        MetadataV2Field? Field = null,
        MetadataV2Service? Service = null,
        MetadataV2Publication? Publication = null,
        MetadataV2Catalog? Catalog = null,
        MetadataV2Policy? Policy = null,
        MetadataV2Role? Role = null)
    {
        public static ResolvedArtifact ForField(string semanticId, MetadataV2Resource parent, MetadataV2Field field)
            => new(
                semanticId,
                MetadataSemanticArtifactKind.Field,
                field.Title ?? field.Name,
                ParentResource: parent,
                Field: field);
    }

    private sealed record DependentState(
        string SemanticId,
        MetadataSemanticArtifactKind SemanticKind,
        string? DisplayName,
        MetadataCompatibilityFindingSeverity ImpactSeverity,
        string Reason,
        string ViaSemanticId,
        string RelationshipKind)
    {
        public MetadataAffectedDependent ToDependent()
            => new()
            {
                SemanticId = SemanticId,
                SemanticKind = SemanticKind,
                DisplayName = DisplayName,
                ImpactSeverity = ImpactSeverity,
                Reason = Reason,
                ViaSemanticId = ViaSemanticId,
                RelationshipKind = RelationshipKind,
            };
    }
}
