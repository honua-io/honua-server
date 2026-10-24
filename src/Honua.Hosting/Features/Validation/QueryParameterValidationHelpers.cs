// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;

namespace Honua.Infrastructure.Validation;

internal static class QueryParameterValidationHelpers
{
    public static string? GetValidationError(
        ICommonQueryValidator validator,
        IReadOnlyCollection<string> parameterNames,
        IReadOnlySet<string> allowedParameters,
        string defaultMessage = "Invalid query parameter.")
    {
        var validationResult = validator.ValidateAllowedParameters(parameterNames, allowedParameters);
        return GetValidationError(validationResult, defaultMessage);
    }

    public static string? GetValidationError(
        ValidationResult validationResult,
        string defaultMessage = "Invalid query parameter.")
    {
        return validationResult.IsValid
            ? null
            : validationResult.ErrorMessage ?? defaultMessage;
    }
}
