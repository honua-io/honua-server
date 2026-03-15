namespace Honua.Validation.Runner.Contracts;

public static class ValidationRequestValidator
{
    public static ValidationRunnerResult Validate(ValidationRunnerRequest request)
    {
        var errors = new List<string>();

        if (!ValidationCatalog.TryGetByKey(request.Target, out var contract))
        {
            errors.Add($"Unsupported target '{request.Target}'.");
            return new ValidationRunnerResult
            {
                Target = request.Target,
                Status = ValidationRunnerStatus.Invalid,
                Errors = errors
            };
        }

        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            errors.Add("BaseUrl must be an absolute URI.");
        }
        else if (baseUri.Scheme is not ("http" or "https"))
        {
            errors.Add("BaseUrl must use http or https.");
        }

        foreach (var output in contract.RequiredTerraformOutputs)
        {
            if (!request.TerraformOutputs.TryGetValue(output, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Missing required Terraform output '{output}'.");
            }
        }

        foreach (var variable in contract.RequiredEnvironmentVariables)
        {
            if (!request.EnvironmentVariables.TryGetValue(variable, out var value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Missing required environment variable '{variable}'.");
            }
        }

        return new ValidationRunnerResult
        {
            Target = request.Target,
            Contract = contract,
            Status = errors.Count == 0 ? ValidationRunnerStatus.Valid : ValidationRunnerStatus.Invalid,
            Errors = errors
        };
    }
}
