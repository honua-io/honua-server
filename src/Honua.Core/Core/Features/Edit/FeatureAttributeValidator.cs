// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;

namespace Honua.Core.Features.Edit;

public static partial class FeatureAttributeValidator
{

    public static ValidationResult<ImmutableDictionary<string, object?>> Validate(
        MetadataV2Object resource,
        IReadOnlyDictionary<string, object?>? attributes,
        AttributeValidationOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var batch = new AttributeValidationBatch(resource, options);
        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                batch.Add(attribute);
            }
        }
        return batch.Complete();
    }

    private sealed class AttributeValidationBatch
    {
        private readonly MetadataV2Object _resource;
        private readonly AttributeValidationOptions _options;
        private readonly CompiledResourceFieldIndex _index;
        private readonly List<string> _errors = [];
        private readonly ImmutableDictionary<string, object?>.Builder _attributes =
            ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);

        public AttributeValidationBatch(MetadataV2Object resource, AttributeValidationOptions options)
        {
            _resource = resource;
            _options = options;
            _index = CompiledResourceFieldIndex.For(resource);
        }

        public void Add(KeyValuePair<string, object?> attribute)
        {
            var (key, rawValue) = attribute;
            if (FeatureMutationAttributes.IsReserved(key))
            {
                _attributes[key] = NormalizeAttributeValue(rawValue);
                return;
            }
            if (!_index.FieldsByName.TryGetValue(key, out var field))
            {
                if (_options.Profile == AttributeValidationProfile.Strict)
                {
                    _errors.Add($"Unknown field '{key}'.");
                }
                return;
            }
            ValidateField(field, rawValue);
        }

        private void ValidateField(MetadataV2Field field, object? rawValue)
        {
            if ((_options.Operation == AttributeMutationOperation.Update &&
                 AttributeMutationPolicy.IsReadOnlyOnUpdate(_resource, field)) ||
                (_index.EditorTrackingFields is not null && _index.EditorTrackingFields.Contains(field.Name)))
            {
                _errors.Add($"Field '{field.Name}' is not editable.");
                return;
            }
            var normalizedValue = NormalizeAttributeValue(rawValue);
            if (_options.Coercion == ExactValueCoercion.CanonicalText)
            {
                normalizedValue = CoerceExactNumber(field, rawValue, normalizedValue);
            }
            var error = ValidateAttributeValue(field, normalizedValue, _options.Profile, _index);
            if (error != null)
            {
                _errors.Add(error);
                return;
            }
            // The compatibility profile accepts wire 0/1 and persists declared Booleans.
            if (_options.Profile == AttributeValidationProfile.GeoServices &&
                field.Type == MetadataV2FieldType.Boolean && normalizedValue is not null &&
                ReadNumericBoolean(normalizedValue) is { } booleanValue)
            {
                normalizedValue = booleanValue;
            }
            _attributes[field.Name] = normalizedValue;
        }

        public ValidationResult<ImmutableDictionary<string, object?>> Complete()
        {
            if (_errors.Count == 0)
            {
                return ValidationResult<ImmutableDictionary<string, object?>>.Success(_attributes.ToImmutable());
            }
            var message = _errors.Count == 1
                ? _errors[0]
                : $"Multiple attribute validation errors: {string.Join("; ", _errors)}";
            return ValidationResult<ImmutableDictionary<string, object?>>.Failure(message);
        }
    }

    private static object? NormalizeAttributeValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is JsonElement jsonElement)
        {
            return JsonElementConverter.ConvertToObject(jsonElement);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeAttributeValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeAttributeValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeAttributeValue(item));
            }
            return list.ToArray();
        }

        return value;
    }

    // Clients without an exact wire type send numbers; the stored value stays canonical text.
    // A JSON number keeps its own digits. Anything that is not a finite number stays unchanged
    // so the type check still rejects it.
    private static object? CoerceExactNumber(MetadataV2Field field, object? rawValue, object? normalizedValue)
        => field.Type switch
        {
            MetadataV2FieldType.Decimal => ExactAttributeValues.FormatDecimalNumber(
                rawValue is JsonElement ? rawValue : normalizedValue) ?? normalizedValue,
            MetadataV2FieldType.TimestampOffset => ExactAttributeValues.FormatEpochMilliseconds(normalizedValue)
                ?? normalizedValue,
            _ => normalizedValue
        };

    private static string? ValidateAttributeValue(
        MetadataV2Field field,
        object? value,
        AttributeValidationProfile mode,
        CompiledResourceFieldIndex index)
    {
        if (value == null)
        {
            return field.Nullable ? null : $"Field '{field.Name}' cannot be null.";
        }

        var typeError = ValidateAgainstFieldType(field, value, mode);
        if (typeError != null)
        {
            return typeError;
        }

        // Domain check applies on top of type validation.
        if (field.Domain is { } domain)
        {
            var domainError = ValidateAgainstDomain(field, value, domain, index);
            if (domainError != null)
            {
                return domainError;
            }
        }

        return null;
    }

}

internal sealed class CompiledResourceFieldIndex
{
    private static readonly ConditionalWeakTable<MetadataV2Object, CompiledResourceFieldIndex> Cache = new();

    private CompiledResourceFieldIndex(
        FrozenDictionary<string, MetadataV2Field> fieldsByName,
        FrozenSet<string>? editorTrackingFields,
        FrozenDictionary<string, CompiledCodedValueDomain>? codedValueDomainsByFieldName)
    {
        FieldsByName = fieldsByName;
        EditorTrackingFields = editorTrackingFields;
        CodedValueDomainsByFieldName = codedValueDomainsByFieldName;
    }

    public FrozenDictionary<string, MetadataV2Field> FieldsByName { get; }

    public FrozenSet<string>? EditorTrackingFields { get; }

    public FrozenDictionary<string, CompiledCodedValueDomain>? CodedValueDomainsByFieldName { get; }

    public static CompiledResourceFieldIndex For(MetadataV2Object resource)
        => Cache.GetValue(resource, Build);

    private static CompiledResourceFieldIndex Build(MetadataV2Object resource)
    {
        var fields = new Dictionary<string, MetadataV2Field>(resource.SchemaFields.Count, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CompiledCodedValueDomain>? codedDomains = null;

        foreach (var field in resource.SchemaFields)
        {
            if (!fields.TryAdd(field.Name, field))
            {
                continue;
            }

            if (field.Domain is { } domain &&
                string.Equals(domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase) &&
                domain.CodedValues.Count > 0)
            {
                codedDomains ??= new Dictionary<string, CompiledCodedValueDomain>(StringComparer.OrdinalIgnoreCase);
                codedDomains[field.Name] = CompiledCodedValueDomain.Build(domain.CodedValues);
            }
        }

        FrozenSet<string>? editorTrackingFields = null;
        if (resource.Editing is not null)
        {
            var trackingFields = Edit.EditorTrackingFields.Names(resource.Editing)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            if (trackingFields.Count > 0)
            {
                editorTrackingFields = trackingFields;
            }
        }

        return new CompiledResourceFieldIndex(
            fields.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            editorTrackingFields,
            codedDomains?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}

internal sealed class CompiledCodedValueDomain
{
    private readonly FrozenSet<string>? _stringCodes;
    private readonly FrozenSet<long>? _longCodes;
    private readonly FrozenSet<double>? _doubleCodes;
    private readonly bool _allowTrue;
    private readonly bool _allowFalse;

    private CompiledCodedValueDomain(
        FrozenSet<string>? stringCodes,
        FrozenSet<long>? longCodes,
        FrozenSet<double>? doubleCodes,
        bool allowTrue,
        bool allowFalse)
    {
        _stringCodes = stringCodes;
        _longCodes = longCodes;
        _doubleCodes = doubleCodes;
        _allowTrue = allowTrue;
        _allowFalse = allowFalse;
    }

    public static CompiledCodedValueDomain Build(IReadOnlyList<MetadataV2CodedValue> codedValues)
    {
        HashSet<string>? stringCodes = null;
        HashSet<long>? longCodes = null;
        HashSet<double>? doubleCodes = null;
        var allowTrue = false;
        var allowFalse = false;

        for (var i = 0; i < codedValues.Count; i++)
        {
            var code = codedValues[i].Code;
            switch (code.ValueKind)
            {
                case JsonValueKind.String:
                    {
                        var text = code.GetString();
                        if (text is not null)
                        {
                            stringCodes ??= new HashSet<string>(StringComparer.Ordinal);
                            stringCodes.Add(text);
                        }
                        break;
                    }
                case JsonValueKind.Number:
                    if (code.TryGetInt64(out var codedLong))
                    {
                        longCodes ??= [];
                        longCodes.Add(codedLong);
                    }
                    else if (code.TryGetDouble(out var codedDouble))
                    {
                        doubleCodes ??= [];
                        doubleCodes.Add(codedDouble);
                    }
                    break;
                case JsonValueKind.True:
                    allowTrue = true;
                    break;
                case JsonValueKind.False:
                    allowFalse = true;
                    break;
            }
        }

        return new CompiledCodedValueDomain(
            stringCodes?.ToFrozenSet(StringComparer.Ordinal),
            longCodes?.ToFrozenSet(),
            doubleCodes?.ToFrozenSet(),
            allowTrue,
            allowFalse);
    }

    public bool Matches(object value)
    {
        if (value is bool b && (b ? _allowTrue : _allowFalse))
        {
            return true;
        }

        if (_longCodes is not null &&
            FeatureAttributeValidator.ReadInt64(value) is { } valueLong &&
            _longCodes.Contains(valueLong))
        {
            return true;
        }

        if (_doubleCodes is not null &&
            FeatureAttributeValidator.ReadDouble(value) is { } valueDouble &&
            _doubleCodes.Contains(valueDouble))
        {
            return true;
        }

        if (_stringCodes is not null)
        {
            var text = value as string ?? value.ToString();
            if (text is not null && _stringCodes.Contains(text))
            {
                return true;
            }
        }

        return false;
    }
}
