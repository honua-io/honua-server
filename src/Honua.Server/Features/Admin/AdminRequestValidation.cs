// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin;

// One wording for data-annotation failures on admin request bodies.
internal static class AdminRequestValidation
{
    internal static string? DataAnnotationError(object request)
    {
        var validationResults = new List<ValidationResult>();
        return Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true)
            ? null
            : $"Validation failed: {string.Join(", ", validationResults.Select(r => r.ErrorMessage))}";
    }
}
