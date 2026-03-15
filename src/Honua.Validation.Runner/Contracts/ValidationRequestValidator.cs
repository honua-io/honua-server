namespace Honua.Validation.Runner.Contracts;

public static class ValidationRequestValidator
{
    public static ValidationRunnerResult Validate(ValidationRunnerRequest request)
    {
        var issues = new List<ValidationIssue>();

        if (!ValidationCatalog.TryGetByKey(request.Target, out var contract))
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationIssueCode.UnsupportedTarget,
                Field = nameof(ValidationRunnerRequest.Target),
                Message = $"Unsupported target '{request.Target}'.",
                SuggestedAction = "Use describe-targets to select one of the supported target keys."
            });
            return new ValidationRunnerResult
            {
                Target = request.Target,
                Phase = ValidationRunnerPhase.RequestValidation,
                Status = ValidationRunnerStatus.Invalid,
                Issues = issues,
                Errors = issues.Select(issue => issue.Message).ToArray()
            };
        }

        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationIssueCode.InvalidBaseUrl,
                Field = nameof(ValidationRunnerRequest.BaseUrl),
                Message = "BaseUrl must be an absolute URI.",
                SuggestedAction = "Provide the externally reachable application URL, including scheme and host."
            });
        }
        else if (baseUri.Scheme is not ("http" or "https"))
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationIssueCode.InvalidBaseUrlScheme,
                Field = nameof(ValidationRunnerRequest.BaseUrl),
                Message = "BaseUrl must use http or https.",
                SuggestedAction = "Use an http or https endpoint for the deployed target."
            });
        }

        foreach (var output in contract.RequiredTerraformOutputs)
        {
            if (!request.TerraformOutputs.TryGetValue(output, out var value) || string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new ValidationIssue
                {
                    Code = ValidationIssueCode.MissingTerraformOutput,
                    Field = output,
                    Message = $"Missing required Terraform output '{output}'.",
                    SuggestedAction = "Expose this value from the target Terraform root and include it in the runner request."
                });
            }
        }

        foreach (var variable in contract.RequiredEnvironmentVariables)
        {
            if (!request.EnvironmentVariables.TryGetValue(variable, out var value) || string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new ValidationIssue
                {
                    Code = ValidationIssueCode.MissingEnvironmentVariable,
                    Field = variable,
                    Message = $"Missing required environment variable '{variable}'.",
                    SuggestedAction = "Provide the required runtime input explicitly instead of relying on CI-local defaults."
                });
            }
        }

        return new ValidationRunnerResult
        {
            Target = request.Target,
            Phase = ValidationRunnerPhase.RequestValidation,
            Contract = contract,
            Status = issues.Count == 0 ? ValidationRunnerStatus.Valid : ValidationRunnerStatus.Invalid,
            Issues = issues,
            Errors = issues.Select(issue => issue.Message).ToArray()
        };
    }
}
