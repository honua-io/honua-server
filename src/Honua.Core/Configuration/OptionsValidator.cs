using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Base class for options validation using data annotations.
/// </summary>
/// <typeparam name="T">The options type to validate</typeparam>
public abstract class OptionsValidator<T> : IValidateOptions<T> where T : class
{
    public ValidateOptionsResult Validate(string? name, T options)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(options, serviceProvider: null, items: null);

        var isValid = Validator.TryValidateObject(options, validationContext, validationResults, validateAllProperties: true);

        if (isValid)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = validationResults.Select(r => r.ErrorMessage ?? "Validation error").ToList();
        return ValidateOptionsResult.Fail(errors);
    }
}