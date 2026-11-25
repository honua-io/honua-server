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
            var error = ValidateAttributeValue(field, normalizedValue);
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

        private string? ValidateAttributeValue(MetadataV2Field field, object? value)
        {
            if (value == null)
            {
                return field.Nullable ? null : $"Field '{field.Name}' cannot be null.";
            }

            var typeError = ValidateAgainstFieldType(field, value, _options.Profile);
            if (typeError != null)
            {
                return typeError;
            }

            return field.Domain is not null
                ? ValidateAgainstDomain(field, value, _index)
                : null;
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
    [Flags]
    private enum AllowedBooleanCodes : byte
    {
        None = 0,
        True = 1,
        False = 2,
    }

    private readonly record struct CodedValueSet
    {
        public FrozenSet<string>? StringCodes { get; init; }

        public FrozenSet<long>? LongCodes { get; init; }

        public FrozenSet<double>? DoubleCodes { get; init; }

        public AllowedBooleanCodes BooleanCodes { get; init; }
    }

    private sealed class CodedValueAccumulator
    {
        private HashSet<string>? _stringCodes;
        private HashSet<long>? _longCodes;
        private HashSet<double>? _doubleCodes;
        private AllowedBooleanCodes _booleanCodes;

        public void Add(JsonElement code)
        {
            switch (code.ValueKind)
            {
                case JsonValueKind.String:
                    AddStringCode(code.GetString());
                    break;
                case JsonValueKind.Number:
                    AddNumericCode(code);
                    break;
                case JsonValueKind.True:
                    _booleanCodes |= AllowedBooleanCodes.True;
                    break;
                case JsonValueKind.False:
                    _booleanCodes |= AllowedBooleanCodes.False;
                    break;
            }
        }

        public CodedValueSet ToCodedValueSet()
            => new()
            {
                StringCodes = _stringCodes?.ToFrozenSet(StringComparer.Ordinal),
                LongCodes = _longCodes?.ToFrozenSet(),
                DoubleCodes = _doubleCodes?.ToFrozenSet(),
                BooleanCodes = _booleanCodes,
            };

        private void AddStringCode(string? text)
        {
            if (text is null)
            {
                return;
            }

            _stringCodes ??= new HashSet<string>(StringComparer.Ordinal);
            _stringCodes.Add(text);
        }

        private void AddNumericCode(JsonElement code)
        {
            if (code.TryGetInt64(out var codedLong))
            {
                _longCodes ??= [];
                _longCodes.Add(codedLong);
            }
            else if (code.TryGetDouble(out var codedDouble))
            {
                _doubleCodes ??= [];
                _doubleCodes.Add(codedDouble);
            }
        }
    }

    private readonly FrozenSet<string>? _stringCodes;
    private readonly FrozenSet<long>? _longCodes;
    private readonly FrozenSet<double>? _doubleCodes;
    private readonly AllowedBooleanCodes _booleanCodes;

    private CompiledCodedValueDomain(CodedValueSet codes)
    {
        _stringCodes = codes.StringCodes;
        _longCodes = codes.LongCodes;
        _doubleCodes = codes.DoubleCodes;
        _booleanCodes = codes.BooleanCodes;
    }

    public static CompiledCodedValueDomain Build(IReadOnlyList<MetadataV2CodedValue> codedValues)
    {
        var accumulator = new CodedValueAccumulator();
        for (var i = 0; i < codedValues.Count; i++)
        {
            accumulator.Add(codedValues[i].Code);
        }

        return new CompiledCodedValueDomain(accumulator.ToCodedValueSet());
    }

    public bool Matches(object value)
    {
        if (value is bool b && ((b ? AllowedBooleanCodes.True : AllowedBooleanCodes.False) & _booleanCodes) != 0)
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
