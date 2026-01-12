// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Validation;

namespace Honua.Server.Features.Infrastructure.Validation;

internal sealed class FeatureMutationValidator
{
    private readonly IGeometryValidator _geometryValidator;

    public FeatureMutationValidator(IGeometryValidator geometryValidator)
    {
        _geometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));
    }

    public ValidationResult<ImmutableDictionary<string, object?>> ValidateAttributes(
        LayerDefinition layer,
        IReadOnlyDictionary<string, object?>? attributes,
        ValidationExtensions.AttributeValidationMode mode)
    {
        return layer.ValidateAttributes(attributes, mode);
    }

    public async Task<GeometryMutationResult> ValidateGeometryAsync(
        byte[]? geometry,
        CancellationToken cancellationToken)
    {
        if (geometry == null || geometry.Length == 0)
        {
            return GeometryMutationResult.Success(geometry);
        }

        var validationResult = await _geometryValidator.ValidateCompleteAsync(geometry, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errorMessages = string.Join("; ", validationResult.Errors.Select(error => error.Message));
            return GeometryMutationResult.Failure(errorMessages);
        }

        var finalGeometry = validationResult.WasRepaired
            ? validationResult.RepairedWkb
            : geometry;

        return GeometryMutationResult.Success(finalGeometry);
    }
}

internal sealed record GeometryMutationResult
{
    public bool IsValid { get; init; }
    public byte[]? Geometry { get; init; }
    public string? ErrorMessage { get; init; }

    public static GeometryMutationResult Success(byte[]? geometry)
        => new() { IsValid = true, Geometry = geometry };

    public static GeometryMutationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}
