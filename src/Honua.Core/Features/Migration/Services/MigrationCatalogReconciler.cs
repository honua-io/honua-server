// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Reconciles a published <see cref="MetadataV2Resource"/> catalog entry against the corresponding
/// source service definition captured in a <see cref="MigrationSourceInventoryArtifact"/>. Counts
/// can match while the schema is wrong; this pass asserts per-field name+type, geometry type,
/// declared SRID, declared extent, ObjectID/GlobalID identifier semantic-roles, and the persistence
/// of inventory-captured domains plus manifest-captured relationships.
/// </summary>
/// <remarks>
/// <para>
/// This service is pure and deterministic: it takes inventory + published catalog observation +
/// optional manifest-captured relationship records and returns a
/// <see cref="MigrationCatalogReconciliationReport"/> with stable finding codes per layer. It is
/// separate from <see cref="MigrationAcceptanceParityStageRunner"/> (which intentionally keeps a
/// small probe set for the dry-run acceptance suite) because the catalog reconciliation pass needs
/// full access to the typed Metadata v2 graph entry that the apply stage persisted.
/// </para>
/// </remarks>
public static class MigrationCatalogReconciler
{
    private const string IdPrimarySemanticRole = "id.primary";
    private const string IdentifierPrimarySemanticRole = "identifier.primary";

    private static readonly StringComparer FieldNameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Reconcile a batch of inventory resources against their published catalog entries.
    /// </summary>
    /// <param name="runId">Stable identifier for this reconciliation run.</param>
    /// <param name="sourceKind">Source kind such as <c>arcgis-geoservices-rest</c>.</param>
    /// <param name="inputs">Per-resource reconciliation inputs.</param>
    /// <returns>Aggregate report with deterministic per-resource outcomes.</returns>
    public static MigrationCatalogReconciliationReport BuildReport(
        string runId,
        string sourceKind,
        IEnumerable<MigrationCatalogReconciliationInput> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentNullException.ThrowIfNull(inputs);

        var outcomes = inputs
            .Select(static input =>
            {
                ArgumentNullException.ThrowIfNull(input);
                if (input.Resource is null)
                {
                    throw new ArgumentException(
                        "Catalog reconciliation inputs must supply an inventory resource.",
                        nameof(inputs));
                }

                return ReconcileResourceInternal(
                    input.Resource,
                    input.PublishedResource,
                    input.ExpectedRelationships,
                    input.SourceBbox,
                    input.TargetResourceId);
            })
            .OrderBy(static outcome => outcome.SourceResourceId, StringComparer.Ordinal)
            .ToArray();

        return new MigrationCatalogReconciliationReport
        {
            RunId = runId,
            SourceKind = sourceKind,
            Summary = BuildSummary(outcomes),
            Resources = outcomes
        };
    }

    /// <summary>
    /// Reconcile a single inventory resource against its published catalog entry. Exposed for
    /// callers (and tests) that drive the reconciler one resource at a time.
    /// </summary>
    public static MigrationCatalogReconciliationResource ReconcileResource(
        MigrationInventoryResource resource,
        MetadataV2Resource? publishedResource,
        IEnumerable<MigrationManifestRelationshipRecord>? expectedRelationships = null,
        double[]? sourceBbox = null,
        string? targetResourceId = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var expected = expectedRelationships?.ToArray() ?? [];
        return ReconcileResourceInternal(resource, publishedResource, expected, sourceBbox, targetResourceId);
    }

    private static MigrationCatalogReconciliationResource ReconcileResourceInternal(
        MigrationInventoryResource resource,
        MetadataV2Resource? published,
        MigrationManifestRelationshipRecord[] expectedRelationships,
        double[]? sourceBbox,
        string? targetResourceId)
    {
        if (published is null)
        {
            var missing = new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.ResourceMissing,
                Severity = MigrationCatalogReconciliationSeverities.Fail,
                Subject = resource.Id,
                Expected = resource.Name,
                Summary = "Source resource has no published catalog entry to reconcile against."
            };

            return new MigrationCatalogReconciliationResource
            {
                SourceResourceId = resource.Id,
                TargetResourceId = targetResourceId,
                Classification = MigrationCatalogReconciliationClassifications.NotApplicable,
                Findings = [missing]
            };
        }

        var findings = new List<MigrationCatalogReconciliationFinding>();

        CollectSchemaFindings(resource, published, findings);
        CollectSpatialFindings(resource, published, sourceBbox, findings);
        CollectIdentifierFindings(resource, published, findings);
        CollectRelationshipFindings(expectedRelationships, published, findings);

        var ordered = findings
            .OrderBy(static finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(static finding => finding.Subject ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

        return new MigrationCatalogReconciliationResource
        {
            SourceResourceId = resource.Id,
            TargetResourceId = targetResourceId ?? (string.IsNullOrWhiteSpace(published.Metadata.Id) ? null : published.Metadata.Id),
            Classification = ClassifyFindings(ordered),
            Findings = ordered
        };
    }

    private static void CollectSchemaFindings(
        MigrationInventoryResource resource,
        MetadataV2Resource published,
        List<MigrationCatalogReconciliationFinding> findings)
    {
        var publishedByName = published.SchemaFields
            .Where(static field => !string.IsNullOrWhiteSpace(field.Name))
            .GroupBy(static field => field.Name, FieldNameComparer)
            .ToDictionary(group => group.Key, group => group.First(), FieldNameComparer);

        foreach (var inventoryField in resource.Fields)
        {
            if (string.IsNullOrWhiteSpace(inventoryField.Name))
            {
                continue;
            }

            if (!publishedByName.TryGetValue(inventoryField.Name, out var publishedField))
            {
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.FieldMissing,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = inventoryField.Name,
                    Expected = inventoryField.FieldType,
                    Summary = $"Inventory field '{inventoryField.Name}' is missing from the published catalog schema."
                });
                continue;
            }

            CollectFieldTypeFinding(inventoryField, publishedField, findings);
            CollectFieldDomainFindings(inventoryField, publishedField, findings);
        }
    }

    private static void CollectFieldTypeFinding(
        MigrationInventoryField inventoryField,
        MetadataV2Field publishedField,
        List<MigrationCatalogReconciliationFinding> findings)
    {
        var expectedType = NormalizeFieldType(inventoryField.FieldType);
        if (expectedType == MetadataV2FieldType.Unknown)
        {
            return;
        }

        if (FieldTypesEquivalent(expectedType, publishedField.Type))
        {
            return;
        }

        findings.Add(new MigrationCatalogReconciliationFinding
        {
            Code = MigrationCatalogReconciliationCodes.FieldTypeMismatch,
            Severity = MigrationCatalogReconciliationSeverities.Fail,
            Subject = inventoryField.Name,
            Expected = expectedType.ToString(),
            Actual = publishedField.Type.ToString(),
            Summary = $"Inventory field '{inventoryField.Name}' type ({inventoryField.FieldType}) does not match the published catalog field type."
        });
    }

    private static void CollectFieldDomainFindings(
        MigrationInventoryField inventoryField,
        MetadataV2Field publishedField,
        List<MigrationCatalogReconciliationFinding> findings)
    {
        var inventoryDomainName = string.IsNullOrWhiteSpace(inventoryField.DomainName) ? null : inventoryField.DomainName.Trim();
        var inventoryDomainType = NormalizeDomainType(inventoryField.DomainType);
        var hasInventoryDomain = inventoryDomainType is not null
            || inventoryDomainName is not null
            || (inventoryField.DomainValues?.Length ?? 0) > 0
            || inventoryField.DomainRange is not null;

        if (!hasInventoryDomain)
        {
            return;
        }

        if (publishedField.Domain is null)
        {
            if (inventoryField.DomainTruncated)
            {
                // Publish-side null is the expected state when the inventory cap
                // dropped over-cap values; surface the situation as informational
                // so operators still see the truncation.
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.DomainTruncated,
                    Severity = MigrationCatalogReconciliationSeverities.Info,
                    Subject = inventoryField.Name,
                    Expected = inventoryDomainName ?? inventoryDomainType,
                    Summary = $"Inventory captured a {inventoryDomainType ?? "domain"} on field '{inventoryField.Name}' that exceeded the capture cap; the published catalog omits values to match."
                });
                return;
            }

            findings.Add(new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.DomainMissing,
                Severity = MigrationCatalogReconciliationSeverities.Fail,
                Subject = inventoryField.Name,
                Expected = inventoryDomainName ?? inventoryDomainType,
                Summary = $"Inventory captured a {inventoryDomainType ?? "domain"} on field '{inventoryField.Name}' but the published field has no domain attached."
            });
            return;
        }

        var publishedDomainType = NormalizeDomainType(publishedField.Domain.Type);
        if (inventoryDomainType is not null &&
            publishedDomainType is not null &&
            !string.Equals(inventoryDomainType, publishedDomainType, StringComparison.Ordinal))
        {
            findings.Add(new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.DomainTypeMismatch,
                Severity = MigrationCatalogReconciliationSeverities.Fail,
                Subject = inventoryField.Name,
                Expected = inventoryDomainType,
                Actual = publishedDomainType,
                Summary = $"Published domain type on field '{inventoryField.Name}' differs from the inventory-captured domain type."
            });
            // A type mismatch makes the per-shape checks below ambiguous;
            // skip them so the operator focuses on the underlying class swap.
            return;
        }

        if (inventoryDomainName is not null &&
            !string.IsNullOrWhiteSpace(publishedField.Domain.Name) &&
            !string.Equals(publishedField.Domain.Name, inventoryDomainName, StringComparison.Ordinal))
        {
            findings.Add(new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.DomainNameMismatch,
                Severity = MigrationCatalogReconciliationSeverities.Warn,
                Subject = inventoryField.Name,
                Expected = inventoryDomainName,
                Actual = publishedField.Domain.Name,
                Summary = $"Published domain name on field '{inventoryField.Name}' differs from the inventory-captured domain name."
            });
        }

        if (inventoryField.DomainValues is { Length: > 0 } captured &&
            string.Equals(inventoryDomainType, "codedValue", StringComparison.Ordinal))
        {
            var publishedCodes = publishedField.Domain.CodedValues
                .Select(static coded => CodeToString(coded.Code))
                .Where(static code => code is not null)
                .ToHashSet(StringComparer.Ordinal);

            var missing = captured
                .Where(value => !publishedCodes.Contains(value.Code))
                .Select(value => value.Code)
                .ToArray();

            if (missing.Length > 0)
            {
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.DomainValuesMismatch,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = inventoryField.Name,
                    Expected = string.Join(",", captured.Select(static value => value.Code)),
                    Actual = string.Join(",", publishedCodes.OrderBy(static value => value, StringComparer.Ordinal)),
                    Summary = $"Coded values captured at inventory time are missing from the published domain on field '{inventoryField.Name}': {string.Join(",", missing)}."
                });
            }
        }

        if (inventoryField.DomainRange is { } inventoryRange &&
            string.Equals(inventoryDomainType, "range", StringComparison.Ordinal))
        {
            var publishedRange = publishedField.Domain.Range;
            string? publishedMin = null;
            string? publishedMax = null;
            if (publishedRange is { Count: >= 2 })
            {
                publishedMin = publishedRange[0].GetRawText();
                publishedMax = publishedRange[1].GetRawText();
            }

            if (publishedMin is null
                || publishedMax is null
                || !string.Equals(publishedMin, inventoryRange.Min, StringComparison.Ordinal)
                || !string.Equals(publishedMax, inventoryRange.Max, StringComparison.Ordinal))
            {
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.DomainRangeMismatch,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = inventoryField.Name,
                    Expected = $"[{inventoryRange.Min},{inventoryRange.Max}]",
                    Actual = publishedMin is null || publishedMax is null
                        ? "[]"
                        : $"[{publishedMin},{publishedMax}]",
                    Summary = $"Published range-domain bounds on field '{inventoryField.Name}' differ from the inventory-captured bounds."
                });
            }
        }
    }

    private static string? NormalizeDomainType(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void CollectSpatialFindings(
        MigrationInventoryResource resource,
        MetadataV2Resource published,
        double[]? sourceBbox,
        List<MigrationCatalogReconciliationFinding> findings)
    {
        var hasInventoryGeometry = !string.IsNullOrWhiteSpace(resource.GeometryType);
        if (hasInventoryGeometry)
        {
            var expectedGeometry = NormalizeGeometryType(resource.GeometryType);
            var publishedGeometry = published.Spatial?.GeometryType ?? MetadataV2GeometryType.None;

            if (expectedGeometry != MetadataV2GeometryType.None && publishedGeometry != expectedGeometry)
            {
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.GeometryTypeMismatch,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = "spatial",
                    Expected = expectedGeometry.ToString(),
                    Actual = publishedGeometry.ToString(),
                    Summary = "Published geometry type does not match the inventory geometry type."
                });
            }
        }

        var expectedSrid = ResolveInventorySrid(resource.SpatialReferences);
        var publishedSrid = published.Spatial?.SpatialReference?.ResolveSrid();
        if (expectedSrid.HasValue && publishedSrid.HasValue && expectedSrid.Value != publishedSrid.Value)
        {
            findings.Add(new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.SridMismatch,
                Severity = MigrationCatalogReconciliationSeverities.Fail,
                Subject = "spatial",
                Expected = expectedSrid.Value.ToString(CultureInfo.InvariantCulture),
                Actual = publishedSrid.Value.ToString(CultureInfo.InvariantCulture),
                Summary = "Published SRID does not match the inventory-declared SRID."
            });
        }

        var normalizedSourceBbox = NormalizeBbox(sourceBbox);
        if (normalizedSourceBbox is not null)
        {
            var publishedBbox = published.Spatial?.Bbox;
            if (publishedBbox is null)
            {
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.ExtentMissing,
                    Severity = MigrationCatalogReconciliationSeverities.Warn,
                    Subject = "spatial",
                    Expected = FormatBbox(normalizedSourceBbox),
                    Summary = "Source declared an extent but the published catalog entry advertises none."
                });
            }
            else
            {
                var publishedAsArray = NormalizeBbox(new[] { publishedBbox.West, publishedBbox.South, publishedBbox.East, publishedBbox.North })!;
                if (!BboxesOverlap(normalizedSourceBbox, publishedAsArray))
                {
                    findings.Add(new MigrationCatalogReconciliationFinding
                    {
                        Code = MigrationCatalogReconciliationCodes.ExtentMismatch,
                        Severity = MigrationCatalogReconciliationSeverities.Warn,
                        Subject = "spatial",
                        Expected = FormatBbox(normalizedSourceBbox),
                        Actual = FormatBbox(publishedAsArray),
                        Summary = "Published extent is disjoint from the source-declared extent."
                    });
                }
            }
        }
    }

    private static void CollectIdentifierFindings(
        MigrationInventoryResource resource,
        MetadataV2Resource published,
        List<MigrationCatalogReconciliationFinding> findings)
    {
        var hasObjectIdInInventory = resource.Fields.Any(static field =>
            !string.IsNullOrWhiteSpace(field.FieldType) &&
            string.Equals(field.FieldType, "esriFieldTypeOID", StringComparison.OrdinalIgnoreCase));

        var hasGlobalIdInInventory = resource.Fields.Any(static field =>
            !string.IsNullOrWhiteSpace(field.FieldType) &&
            string.Equals(field.FieldType, "esriFieldTypeGlobalID", StringComparison.OrdinalIgnoreCase));

        if (hasObjectIdInInventory)
        {
            var hasPrimaryRole = published.SchemaFields.Any(static field =>
                field.SemanticRoles.Contains(IdPrimarySemanticRole, StringComparer.OrdinalIgnoreCase) ||
                field.SemanticRoles.Contains(IdentifierPrimarySemanticRole, StringComparer.OrdinalIgnoreCase));

            if (!hasPrimaryRole)
            {
                findings.Add(new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.ObjectIdMissing,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = "identifier",
                    Expected = IdPrimarySemanticRole,
                    Summary = "Inventory advertised an ObjectID field but no published schema field carries the primary identifier semantic role."
                });
            }
        }

        if (hasGlobalIdInInventory && string.IsNullOrWhiteSpace(published.Editing?.GlobalIdField))
        {
            var globalIdInventoryField = resource.Fields.First(static field =>
                string.Equals(field.FieldType, "esriFieldTypeGlobalID", StringComparison.OrdinalIgnoreCase));
            findings.Add(new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.GlobalIdMissing,
                Severity = MigrationCatalogReconciliationSeverities.Warn,
                Subject = "identifier",
                Expected = globalIdInventoryField.Name,
                Summary = "Inventory advertised a GlobalID field but the published catalog entry has no GlobalIdField binding."
            });
        }
    }

    private static void CollectRelationshipFindings(
        MigrationManifestRelationshipRecord[] expectedRelationships,
        MetadataV2Resource published,
        List<MigrationCatalogReconciliationFinding> findings)
    {
        if (expectedRelationships.Length == 0)
        {
            return;
        }

        var publishedIds = published.Relationships
            .Select(static relationship => relationship.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var publishedNames = published.Relationships
            .Select(static relationship => relationship.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expected in expectedRelationships)
        {
            // Only assert persistence for relationships the manifest classified as automated or
            // assisted. Manual-review and unsupported relationships are intentionally captured but
            // not persisted by the apply stage.
            if (!IsRelationshipExpectedToPersist(expected.Classification))
            {
                continue;
            }

            var matched = publishedIds.Contains(expected.SourceRelationshipId)
                || (!string.IsNullOrWhiteSpace(expected.TargetRelationshipRef) && publishedIds.Contains(expected.TargetRelationshipRef))
                || (!string.IsNullOrWhiteSpace(expected.Name) && publishedNames.Contains(expected.Name));

            if (matched)
            {
                continue;
            }

            findings.Add(new MigrationCatalogReconciliationFinding
            {
                Code = MigrationCatalogReconciliationCodes.RelationshipMissing,
                Severity = MigrationCatalogReconciliationSeverities.Fail,
                Subject = expected.SourceRelationshipId,
                Expected = expected.Name ?? expected.SourceRelationshipId,
                Summary = $"Relationship '{expected.SourceRelationshipId}' captured at manifest time is missing from the published catalog entry."
            });
        }
    }

    private static bool IsRelationshipExpectedToPersist(string classification)
        => string.Equals(classification, MigrationManifestRelationshipClassifications.Automated, StringComparison.Ordinal)
        || string.Equals(classification, MigrationManifestRelationshipClassifications.Assisted, StringComparison.Ordinal);

    private static MigrationCatalogReconciliationSummary BuildSummary(
        MigrationCatalogReconciliationResource[] outcomes)
    {
        var pass = 0;
        var warn = 0;
        var fail = 0;
        var notApplicable = 0;
        var findings = 0;

        foreach (var outcome in outcomes)
        {
            switch (outcome.Classification)
            {
                case MigrationCatalogReconciliationClassifications.Pass: pass++; break;
                case MigrationCatalogReconciliationClassifications.Warn: warn++; break;
                case MigrationCatalogReconciliationClassifications.Fail: fail++; break;
                default: notApplicable++; break;
            }

            findings += outcome.Findings.Length;
        }

        return new MigrationCatalogReconciliationSummary
        {
            ResourceCount = outcomes.Length,
            PassResourceCount = pass,
            WarnResourceCount = warn,
            FailResourceCount = fail,
            NotApplicableResourceCount = notApplicable,
            FindingCount = findings
        };
    }

    private static string ClassifyFindings(MigrationCatalogReconciliationFinding[] findings)
    {
        if (findings.Length == 0)
        {
            return MigrationCatalogReconciliationClassifications.Pass;
        }

        if (findings.Any(static finding => finding.Severity == MigrationCatalogReconciliationSeverities.Fail))
        {
            return MigrationCatalogReconciliationClassifications.Fail;
        }

        if (findings.Any(static finding => finding.Severity == MigrationCatalogReconciliationSeverities.Warn))
        {
            return MigrationCatalogReconciliationClassifications.Warn;
        }

        return MigrationCatalogReconciliationClassifications.Pass;
    }

    private static MetadataV2FieldType NormalizeFieldType(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return MetadataV2FieldType.Unknown;
        }

        var trimmed = source.Trim();
        if (trimmed.StartsWith("esriFieldType", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.AsSpan(13).ToString().ToLowerInvariant() switch
            {
                "oid" => MetadataV2FieldType.Integer,
                "smallinteger" => MetadataV2FieldType.Integer,
                "integer" => MetadataV2FieldType.Integer,
                "integer64" or "biginteger" => MetadataV2FieldType.BigInteger,
                "single" => MetadataV2FieldType.Float,
                "double" => MetadataV2FieldType.Double,
                "string" => MetadataV2FieldType.String,
                "date" => MetadataV2FieldType.DateTime,
                "blob" or "raster" or "xml" => MetadataV2FieldType.Binary,
                "guid" or "globalid" => MetadataV2FieldType.Uuid,
                "geometry" => MetadataV2FieldType.Geometry,
                _ => MetadataV2FieldType.Unknown
            };
        }

        var paren = trimmed.IndexOf('(');
        var bare = (paren < 0 ? trimmed : trimmed[..paren]).Trim().ToLowerInvariant();
        return bare switch
        {
            "string" or "text" or "varchar" or "character" or "char" or "character varying" => MetadataV2FieldType.String,
            "int" or "integer" or "int4" or "smallint" or "int2" => MetadataV2FieldType.Integer,
            "long" or "bigint" or "int8" or "int64" => MetadataV2FieldType.BigInteger,
            "double" or "double precision" or "float8" or "number" or "numeric" or "decimal" => MetadataV2FieldType.Double,
            "float" or "real" or "float4" or "single" => MetadataV2FieldType.Float,
            "bool" or "boolean" => MetadataV2FieldType.Boolean,
            "datetime" or "timestamp" or "timestamptz" or "date-time" => MetadataV2FieldType.DateTime,
            "date" => MetadataV2FieldType.Date,
            "time" => MetadataV2FieldType.Time,
            "uuid" or "guid" => MetadataV2FieldType.Uuid,
            "json" or "jsonb" or "object" => MetadataV2FieldType.Json,
            "binary" or "bytea" or "blob" => MetadataV2FieldType.Binary,
            "geometry" => MetadataV2FieldType.Geometry,
            "geography" => MetadataV2FieldType.Geography,
            _ => MetadataV2FieldType.Unknown
        };
    }

    private static bool FieldTypesEquivalent(MetadataV2FieldType expected, MetadataV2FieldType actual)
    {
        if (expected == actual)
        {
            return true;
        }

        // Integer widening is acceptable when the published catalog promotes a 32-bit source to a
        // 64-bit canonical column. Narrowing in the opposite direction is reported as a mismatch
        // because it loses headroom.
        if (expected == MetadataV2FieldType.Integer && actual == MetadataV2FieldType.BigInteger)
        {
            return true;
        }

        if (expected == MetadataV2FieldType.Float && actual == MetadataV2FieldType.Double)
        {
            return true;
        }

        if ((expected == MetadataV2FieldType.Geometry && actual == MetadataV2FieldType.Geography) ||
            (expected == MetadataV2FieldType.Geography && actual == MetadataV2FieldType.Geometry))
        {
            return true;
        }

        return false;
    }

    private static MetadataV2GeometryType NormalizeGeometryType(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return MetadataV2GeometryType.None;
        }

        var trimmed = source.Trim();
        if (trimmed.StartsWith("esriGeometry", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.AsSpan(12).ToString().ToLowerInvariant() switch
            {
                "point" => MetadataV2GeometryType.Point,
                "multipoint" => MetadataV2GeometryType.MultiPoint,
                "polyline" => MetadataV2GeometryType.MultiLineString,
                "polygon" => MetadataV2GeometryType.MultiPolygon,
                "envelope" => MetadataV2GeometryType.Polygon,
                "multipatch" => MetadataV2GeometryType.GeometryCollection,
                _ => MetadataV2GeometryType.None
            };
        }

        return trimmed.ToLowerInvariant() switch
        {
            "point" => MetadataV2GeometryType.Point,
            "multipoint" => MetadataV2GeometryType.MultiPoint,
            "linestring" or "line" => MetadataV2GeometryType.LineString,
            "multilinestring" => MetadataV2GeometryType.MultiLineString,
            "polygon" => MetadataV2GeometryType.Polygon,
            "multipolygon" => MetadataV2GeometryType.MultiPolygon,
            "geometrycollection" => MetadataV2GeometryType.GeometryCollection,
            "mixed" => MetadataV2GeometryType.Mixed,
            "none" => MetadataV2GeometryType.None,
            _ => MetadataV2GeometryType.None
        };
    }

    private static int? ResolveInventorySrid(MigrationSpatialReferenceInfo[] references)
    {
        if (references.Length == 0)
        {
            return null;
        }

        // Prefer the declared/native role; fall back to the first reference with a resolvable SRID.
        foreach (var preferredRole in new[] { "declared", "native", "service" })
        {
            var match = references.FirstOrDefault(reference =>
                string.Equals(reference.Role, preferredRole, StringComparison.OrdinalIgnoreCase) &&
                reference.Srid.HasValue);
            if (match is not null)
            {
                return match.Srid;
            }
        }

        return references.FirstOrDefault(static reference => reference.Srid.HasValue)?.Srid;
    }

    private static double[]? NormalizeBbox(double[]? bbox)
    {
        if (bbox is null || bbox.Length != 4)
        {
            return null;
        }

        var minX = Math.Min(bbox[0], bbox[2]);
        var minY = Math.Min(bbox[1], bbox[3]);
        var maxX = Math.Max(bbox[0], bbox[2]);
        var maxY = Math.Max(bbox[1], bbox[3]);
        return [minX, minY, maxX, maxY];
    }

    private static bool BboxesOverlap(double[] a, double[] b)
        => a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];

    private static string FormatBbox(double[] bbox)
        => string.Create(CultureInfo.InvariantCulture,
            $"[{bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]}]");

    private static string? CodeToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }
}
