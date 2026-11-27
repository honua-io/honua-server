// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Server.Features.Console;

internal static class ConsoleRequestValidationHelpers
{
    public static string? Validate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TRequest>(TRequest request)
        where TRequest : notnull
    {
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(request) { DisplayName = typeof(TRequest).Name };
        if (Validator.TryValidateObject(request, context, validationResults, validateAllProperties: true))
        {
            return null;
        }

        return string.Join(", ", validationResults.Select(r => r.ErrorMessage));
    }
}
