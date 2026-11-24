// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Console;

internal static class ConsoleRequestValidationHelpers
{
    public static bool TryValidate<TRequest>(TRequest request, out string error) where TRequest : notnull
    {
        var validationResults = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }

        error = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
        return false;
    }
}
