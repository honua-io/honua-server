// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2CustomFieldSchema : ExtensibleJsonRecord
{
    public const int MaxSchemasPerEnvironment = 64;
    public const int MaxNameLength = 64;
    public const int MaxTitleLength = 256;
    public const int MaxDescriptionLength = 2048;
    public const int MaxAllowedValues = 200;
    public const int MaxValueLength = 2048;
    public const int MaxListItems = 100;

    private IReadOnlyList<string>? _allowedValues;
    private IReadOnlyList<string>? _appliesTo;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = MetadataV2CustomFieldTypes.Text;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("allowedValues")]
    public IReadOnlyList<string> AllowedValues
    {
        get => _allowedValues ?? Array.Empty<string>();
        init => _allowedValues = value;
    }

    // Empty means the custom field applies to every item kind in the environment.
    [JsonPropertyName("appliesTo")]
    public IReadOnlyList<string> AppliesTo
    {
        get => _appliesTo ?? Array.Empty<string>();
        init => _appliesTo = value;
    }

    [JsonPropertyName("facetable")]
    public bool Facetable { get; init; } = true;

    public bool AppliesToKind(string itemKind)
    {
        if (AppliesTo.Count == 0)
        {
            return true;
        }

        var normalized = MetadataV2CustomFieldKinds.Normalize(itemKind);
        return AppliesTo.Any(kind => string.Equals(
            MetadataV2CustomFieldKinds.Normalize(kind),
            normalized,
            StringComparison.Ordinal));
    }
}

public static class MetadataV2CustomFieldTypes
{
    public const string Text = "string";
    public const string Number = "number";
    public const string BooleanValue = "boolean";
    public const string Date = "date";
    public const string EnumValue = "enum";
    public const string TextList = "stringList";

    public static FrozenSet<string> All { get; } = FrozenSet.ToFrozenSet(
        [Text, Number, BooleanValue, Date, EnumValue, TextList],
        StringComparer.Ordinal);
}

public static class MetadataV2CustomFieldKinds
{
    public const string Dataset = "dataset";
    public const string Service = "service";
    public const string Layer = "layer";
    public const string Folder = "folder";
    public const string Catalog = "catalog";
    public const string Graph = "graph";

    public static FrozenSet<string> All { get; } = FrozenSet.ToFrozenSet(
        [Dataset, Service, Layer, Folder, Catalog, Graph],
        StringComparer.Ordinal);

    public static string Normalize(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "resource" or "resources" or "object" or "objects" or "dataset" or "datasets" => Dataset,
        "service" or "services" => Service,
        "publication" or "publications" or "layer" or "layers" => Layer,
        "folder" or "folders" => Folder,
        "catalog" or "catalogs" => Catalog,
        "graph" or "environment" => Graph,
        var other => other ?? string.Empty,
    };
}

public sealed record MetadataV2CustomFieldOwner(
    string Label,
    string ItemKind,
    IReadOnlyDictionary<string, JsonElement>? CustomFields);

public static partial class MetadataV2CustomFieldValidation
{
    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNamePattern();

    public static void ValidateSchemas(List<string> errors, IReadOnlyList<MetadataV2CustomFieldSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schemas);

        if (schemas.Count > MetadataV2CustomFieldSchema.MaxSchemasPerEnvironment)
        {
            errors.Add($"customFieldSchemas must not exceed {MetadataV2CustomFieldSchema.MaxSchemasPerEnvironment} entries.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < schemas.Count; i++)
        {
            errors.AddRange(SchemaErrors($"customFieldSchemas[{i}]", schemas[i], seen));
        }
    }

    // Stored documents: values must match their schema, but required fields and undeclared keys are
    // an editor concern, so publishing, imports and seeding never fail on a field they cannot supply.
    public static void ValidateItemCustomFields(
        List<string> errors,
        MetadataV2CustomFieldOwner owner,
        IReadOnlyList<MetadataV2CustomFieldSchema> schemas) =>
        new ItemCustomFieldsValidator(errors, owner, schemas).Validate(static (_, _) => { });

    // Editor saves that keep the stored custom fields: required fields must be present.
    public static void ValidateRequiredItemCustomFields(
        List<string> errors,
        MetadataV2CustomFieldOwner owner,
        IReadOnlyList<MetadataV2CustomFieldSchema> schemas)
    {
        var validator = new ItemCustomFieldsValidator(errors, owner, schemas);
        validator.ValidateRequired();
        validator.Validate(static (_, _) => { });
    }

    // Editor saves that write custom fields: required fields must be present and every key declared.
    public static void ValidateDeclaredItemCustomFields(
        List<string> errors,
        MetadataV2CustomFieldOwner owner,
        IReadOnlyList<MetadataV2CustomFieldSchema> schemas)
    {
        var validator = new ItemCustomFieldsValidator(errors, owner, schemas);
        validator.ValidateRequired();
        validator.Validate(static (validator, key) => validator.Add(
            $"{key} is not declared for item kind '{MetadataV2CustomFieldKinds.Normalize(validator.Owner.ItemKind)}'."));
    }

    private static IEnumerable<string> SchemaErrors(string prefix, MetadataV2CustomFieldSchema? schema, HashSet<string> seen)
    {
        if (schema is null)
        {
            yield return $"{prefix} must not be null.";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(schema.Name) || !FieldNamePattern().IsMatch(schema.Name))
        {
            yield return $"{prefix}.name must start with a lowercase ASCII letter and contain only [a-z0-9_-] (max {MetadataV2CustomFieldSchema.MaxNameLength} chars).";
        }
        else if (!seen.Add(schema.Name))
        {
            yield return $"{prefix}.name '{schema.Name}' is duplicated.";
        }

        if (!MetadataV2CustomFieldTypes.All.Contains(schema.Type))
        {
            yield return $"{prefix}.type '{schema.Type}' is invalid. Supported types: {string.Join(", ", MetadataV2CustomFieldTypes.All.Order(StringComparer.Ordinal))}.";
        }

        foreach (var error in TextErrors(prefix, schema).Concat(AllowedValueErrors(prefix, schema)).Concat(AppliesToErrors(prefix, schema)))
        {
            yield return error;
        }
    }

    private static IEnumerable<string> TextErrors(string prefix, MetadataV2CustomFieldSchema schema)
    {
        if (!IsValidText(schema.Title, MetadataV2CustomFieldSchema.MaxTitleLength))
        {
            yield return $"{prefix}.title exceeds {MetadataV2CustomFieldSchema.MaxTitleLength} characters or contains control characters.";
        }

        if (!IsValidText(schema.Description, MetadataV2CustomFieldSchema.MaxDescriptionLength))
        {
            yield return $"{prefix}.description exceeds {MetadataV2CustomFieldSchema.MaxDescriptionLength} characters or contains control characters.";
        }
    }

    private static IEnumerable<string> AllowedValueErrors(string prefix, MetadataV2CustomFieldSchema schema)
    {
        if (schema.AllowedValues.Count > MetadataV2CustomFieldSchema.MaxAllowedValues)
        {
            yield return $"{prefix}.allowedValues must not exceed {MetadataV2CustomFieldSchema.MaxAllowedValues} entries.";
        }

        if (string.Equals(schema.Type, MetadataV2CustomFieldTypes.EnumValue, StringComparison.Ordinal)
            && schema.AllowedValues.Count == 0)
        {
            yield return $"{prefix}.allowedValues is required for enum fields.";
        }

        if (schema.AllowedValues.Any(allowed => !IsNonEmptyText(allowed)))
        {
            yield return $"{prefix}.allowedValues must contain nonempty strings without control characters.";
        }
    }

    private static IEnumerable<string> AppliesToErrors(string prefix, MetadataV2CustomFieldSchema schema) =>
        schema.AppliesTo
            .Where(kind => !MetadataV2CustomFieldKinds.All.Contains(MetadataV2CustomFieldKinds.Normalize(kind)))
            .Select(kind => $"{prefix}.appliesTo contains unknown item kind '{kind}'.");

    private static bool IsMissingRequiredValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false,
    };

    private static string? ValueError(MetadataV2CustomFieldSchema schema, JsonElement value) => schema.Type switch
    {
        MetadataV2CustomFieldTypes.Text when value.ValueKind != JsonValueKind.String
            || !IsValidText(value.GetString(), MetadataV2CustomFieldSchema.MaxValueLength) =>
            $"must be a string (max {MetadataV2CustomFieldSchema.MaxValueLength} chars, no control chars).",
        MetadataV2CustomFieldTypes.Number when value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) =>
            "must be a finite number.",
        MetadataV2CustomFieldTypes.BooleanValue when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) =>
            "must be a boolean.",
        MetadataV2CustomFieldTypes.Date when value.ValueKind != JsonValueKind.String || !IsIsoDateOrTimestamp(value.GetString()) =>
            "must be an ISO 8601 date or timestamp string.",
        MetadataV2CustomFieldTypes.EnumValue when value.ValueKind != JsonValueKind.String
            || !schema.AllowedValues.Contains(value.GetString()!, StringComparer.Ordinal) =>
            "must be one of the declared allowed values.",
        MetadataV2CustomFieldTypes.TextList => TextListError(value),
        _ => null,
    };

    private static string? TextListError(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MetadataV2CustomFieldSchema.MaxListItems)
        {
            return $"must be an array of at most {MetadataV2CustomFieldSchema.MaxListItems} strings.";
        }

        return value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String && IsNonEmptyText(item.GetString()))
            ? null
            : "must contain nonempty strings without control characters.";
    }

    private static bool IsNonEmptyText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IsValidText(value, MetadataV2CustomFieldSchema.MaxValueLength);

    private sealed class ItemCustomFieldsValidator
    {
        private readonly List<string> _errors;
        private readonly MetadataV2CustomFieldSchema[] _applicable;
        private readonly Dictionary<string, MetadataV2CustomFieldSchema> _byName = new(StringComparer.Ordinal);

        public ItemCustomFieldsValidator(List<string> errors, MetadataV2CustomFieldOwner owner, IReadOnlyList<MetadataV2CustomFieldSchema> schemas)
        {
            ArgumentNullException.ThrowIfNull(errors);
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(schemas);
            _errors = errors;
            Owner = owner;
            _applicable = schemas.Where(schema => schema is not null && schema.AppliesToKind(owner.ItemKind)).ToArray();
            foreach (var schema in _applicable)
            {
                _byName.TryAdd(schema.Name, schema);
            }
        }

        public MetadataV2CustomFieldOwner Owner { get; }

        public void Add(string keyAndMessage) => _errors.Add($"{Owner.Label} metadata.customFields.{keyAndMessage}");

        public void ValidateRequired()
        {
            var fields = Owner.CustomFields;
            foreach (var schema in _applicable.Where(static s => s.Required))
            {
                if (fields is null || !fields.TryGetValue(schema.Name, out var value) || IsMissingRequiredValue(value))
                {
                    Add($"{schema.Name} is required.");
                }
            }
        }

        public void Validate(Action<ItemCustomFieldsValidator, string> onUndeclared)
        {
            foreach (var (key, value) in Owner.CustomFields ?? new Dictionary<string, JsonElement>())
            {
                ValidateField(key, value, onUndeclared);
            }
        }

        private void ValidateField(string key, JsonElement value, Action<ItemCustomFieldsValidator, string> onUndeclared)
        {
            if (!FieldNamePattern().IsMatch(key))
            {
                Add($"{key} has an invalid field name.");
            }
            else if (!_byName.TryGetValue(key, out var schema))
            {
                onUndeclared(this, key);
            }
            else if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && ValueError(schema, value) is { } error)
            {
                Add($"{key} {error}");
            }
        }
    }

    private static bool IsIsoDateOrTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }

    private static bool IsValidText(string? value, int limit) =>
        value is null || (value.Length <= limit && !value.Any(static c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')));
}
