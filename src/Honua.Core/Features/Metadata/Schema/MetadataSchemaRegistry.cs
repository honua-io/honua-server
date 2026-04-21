// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Schema;

/// <summary>
/// Default schema registry for metadata resources.
/// </summary>
public sealed class MetadataSchemaRegistry : IMetadataSchemaRegistry
{
    /// <summary>
    /// Current API version supported by the registry.
    /// </summary>
    public const string CurrentVersion = "honua.io/v1alpha1";

    /// <summary>
    /// Legacy API version supported for up-conversion.
    /// </summary>
    public const string LegacyVersion = "honua.io/v1alpha0";

    private static readonly string[] _resourcePayloadRequiredErrors = ["Resource payload is required."];
    private readonly Dictionary<(string ApiVersion, string Kind), ResourceSchemaDefinition> _schemas;

    /// <summary>
    /// Initializes a new schema registry with built-in resource kinds.
    /// </summary>
    public MetadataSchemaRegistry()
    {
        _schemas = BuildSchemas();
    }

    /// <inheritdoc />
    public string CurrentApiVersion => CurrentVersion;

    /// <inheritdoc />
    public string LegacyApiVersion => LegacyVersion;

    /// <inheritdoc />
    public IReadOnlyCollection<ResourceSchemaDefinition> Schemas => _schemas.Values.ToArray();

    /// <inheritdoc />
    public MetadataSchemaValidationResult ValidateAndUpgrade(MetadataResource resource)
    {
        var errors = new List<string>();
        if (resource == null)
        {
            return new MetadataSchemaValidationResult(false, null, _resourcePayloadRequiredErrors, false);
        }

        if (string.IsNullOrWhiteSpace(resource.ApiVersion))
        {
            errors.Add("apiVersion is required.");
        }

        if (string.IsNullOrWhiteSpace(resource.Kind))
        {
            errors.Add("kind is required.");
        }

        if (resource.Metadata == null)
        {
            errors.Add("metadata is required.");
        }
        else if (string.IsNullOrWhiteSpace(resource.Metadata.Name))
        {
            errors.Add("metadata.name is required.");
        }

        if (errors.Count > 0)
        {
            return new MetadataSchemaValidationResult(false, null, errors, false);
        }

        var apiVersion = resource.ApiVersion!;
        var kind = resource.Kind!;
        var wasUpConverted = false;

        if (!string.Equals(apiVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(apiVersion, LegacyVersion, StringComparison.OrdinalIgnoreCase))
            {
                resource = UpgradeLegacyResource(resource);
                apiVersion = resource.ApiVersion!;
                kind = resource.Kind!;
                wasUpConverted = true;
            }
            else
            {
                errors.Add($"Unsupported apiVersion '{apiVersion}'.");
                return new MetadataSchemaValidationResult(false, null, errors, false);
            }
        }

        if (!_schemas.TryGetValue((apiVersion, kind), out var schema))
        {
            errors.Add($"Unsupported kind '{kind}' for apiVersion '{apiVersion}'.");
            return new MetadataSchemaValidationResult(false, null, errors, wasUpConverted);
        }

        if (resource.Spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add("spec must be a JSON object.");
        }
        else if (schema.RequiredSpecFields.Count > 0)
        {
            var spec = resource.Spec;
            foreach (var field in schema.RequiredSpecFields)
            {
                if (!spec.TryGetProperty(field, out _))
                {
                    errors.Add($"spec.{field} is required.");
                }
            }
        }

        if (errors.Count > 0)
        {
            return new MetadataSchemaValidationResult(false, null, errors, wasUpConverted);
        }

        return new MetadataSchemaValidationResult(true, resource, Array.Empty<string>(), wasUpConverted);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSupportedApiVersions(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return Array.Empty<string>();
        }

        return _schemas.Keys
            .Where(key => key.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Select(key => key.ApiVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MetadataResource UpgradeLegacyResource(MetadataResource resource)
    {
        var metadata = resource.Metadata ?? new ResourceMetadata();
        var annotations = metadata.Annotations == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata.Annotations, StringComparer.OrdinalIgnoreCase);

        if (!annotations.ContainsKey(MetadataAnnotations.UpConvertedFrom))
        {
            annotations[MetadataAnnotations.UpConvertedFrom] = LegacyVersion;
        }

        metadata = metadata with
        {
            Annotations = annotations
        };

        return new MetadataResource
        {
            ApiVersion = CurrentVersion,
            Kind = resource.Kind,
            Metadata = metadata,
            Spec = resource.Spec.Clone(),
            Status = resource.Status?.Clone()
        };
    }

    private static Dictionary<(string ApiVersion, string Kind), ResourceSchemaDefinition> BuildSchemas()
    {
        var definitions = new[]
        {
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.Service,
                Description = "Service metadata resource",
                RequiredSpecFields = new[] { "description", "srid" }
            },
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.Layer,
                Description = "Layer metadata resource",
                RequiredSpecFields = new[] { "tableName", "schemaName", "geometryType", "srid" }
            },
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.Relationship,
                Description = "Relationship metadata resource",
                RequiredSpecFields = new[] { "originLayerId", "relatedLayerId", "name", "relationshipType", "originForeignKey", "destinationForeignKey" }
            },
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.Style,
                Description = "Layer style metadata resource",
                RequiredSpecFields = new[] { "layerId", "style" }
            },
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.Connection,
                Description = "Secure connection metadata resource",
                RequiredSpecFields = new[] { "name", "host", "databaseName" }
            },
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.MapTemplate,
                Description = "Map template metadata resource",
                RequiredSpecFields = new[] { "name", "category" }
            },
            new ResourceSchemaDefinition
            {
                ApiVersion = CurrentVersion,
                Kind = MetadataResourceKinds.Theme,
                Description = "Theme metadata resource",
                RequiredSpecFields = new[] { "name" }
            }
        };

        var registry = new Dictionary<(string ApiVersion, string Kind), ResourceSchemaDefinition>(
            definitions.Length,
            new ApiKindComparer());

        foreach (var definition in definitions)
        {
            registry[(definition.ApiVersion, definition.Kind)] = definition;
        }

        return registry;
    }

    private sealed class ApiKindComparer : IEqualityComparer<(string ApiVersion, string Kind)>
    {
        public bool Equals((string ApiVersion, string Kind) x, (string ApiVersion, string Kind) y)
        {
            return string.Equals(x.ApiVersion, y.ApiVersion, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string ApiVersion, string Kind) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ApiVersion),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind));
        }
    }
}
