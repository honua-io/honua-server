namespace Honua.Validation.Runner.Contracts;

public static class ValidationExampleFactory
{
    public static ValidationRunnerRequest Create(ValidationTargetContract contract)
    {
        var terraformOutputs = contract.RequiredTerraformOutputs.ToDictionary(
            key => key,
            key => $"<terraform-output:{key}>",
            StringComparer.Ordinal);

        var environmentVariables = contract.RequiredEnvironmentVariables.ToDictionary(
            key => key,
            key => $"<env:{key}>",
            StringComparer.Ordinal);

        return new ValidationRunnerRequest
        {
            Target = contract.Key,
            BaseUrl = $"https://{contract.Key}.example.honua.dev",
            DataTopology = contract.DefaultDataTopology,
            CleanupPolicy = contract.DefaultCleanupPolicy,
            TerraformOutputs = terraformOutputs,
            EnvironmentVariables = environmentVariables
        };
    }
}
