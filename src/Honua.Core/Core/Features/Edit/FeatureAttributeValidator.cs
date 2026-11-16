// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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
        private readonly Dictionary<string, MetadataV2Field> _fields = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _errors = [];
        private readonly ImmutableDictionary<string, object?>.Builder _attributes =
            ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);

        public AttributeValidationBatch(MetadataV2Object resource, AttributeValidationOptions options)
        {
            _resource = resource;
            _options = options;
            foreach (var field in resource.SchemaFields)
            {
                _fields.TryAdd(field.Name, field);
            }
        }

        public void Add(KeyValuePair<string, object?> attribute)
        {
            var (key, rawValue) = attribute;
            if (FeatureMutationAttributes.IsReserved(key))
            {
                _attributes[key] = NormalizeAttributeValue(rawValue);
                return;
            }
            if (!_fields.TryGetValue(key, out var field))
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
            if (_options.Operation == AttributeMutationOperation.Update &&
                AttributeMutationPolicy.IsReadOnlyOnUpdate(_resource, field))
            {
                _errors.Add($"Field '{field.Name}' is not editable.");
                return;
            }
            var normalizedValue = NormalizeAttributeValue(rawValue);
            var error = ValidateAttributeValue(field, normalizedValue, _options.Profile);
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

    private static string? ValidateAttributeValue(
        MetadataV2Field field,
        object? value,
        AttributeValidationProfile mode)
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
            var domainError = ValidateAgainstDomain(field, value, domain);
            if (domainError != null)
            {
                return domainError;
            }
        }

        return null;
    }

}
