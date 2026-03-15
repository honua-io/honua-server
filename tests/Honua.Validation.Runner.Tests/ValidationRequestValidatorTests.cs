using Honua.Validation.Runner.Contracts;

namespace Honua.Validation.Runner.Tests;

public sealed class ValidationRequestValidatorTests
{
    [Fact]
    public void Validate_WithCompleteAzureFunctionsRequest_ReturnsValid()
    {
        var request = new ValidationRunnerRequest
        {
            Target = "azure-functions",
            BaseUrl = "https://functions.example.honua.dev",
            TerraformOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["honua_url"] = "https://functions.example.honua.dev",
                ["resource_group_name"] = "rg-example",
                ["function_app_name"] = "fn-example"
            },
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HONUA_FUNCTIONS_IMAGE"] = "registry.example/honua-server:functions"
            }
        };

        var result = ValidationRequestValidator.Validate(request);

        Assert.Equal(ValidationRunnerStatus.Valid, result.Status);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Contract);
        Assert.Equal("azure-functions", result.Contract!.Key);
    }

    [Fact]
    public void Validate_WithMissingTerraformOutputs_ReturnsInvalid()
    {
        var request = new ValidationRunnerRequest
        {
            Target = "aws-ecs",
            BaseUrl = "https://ecs.example.honua.dev",
            TerraformOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["honua_url"] = "https://ecs.example.honua.dev"
            },
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HONUA_AWS_ECS_IMAGE"] = "123456789012.dkr.ecr.us-west-2.amazonaws.com/honua-server:ecs"
            }
        };

        var result = ValidationRequestValidator.Validate(request);

        Assert.Equal(ValidationRunnerStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, error => error.Contains("db_endpoint", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("control_plane_backend_name", StringComparison.Ordinal));
    }
}
