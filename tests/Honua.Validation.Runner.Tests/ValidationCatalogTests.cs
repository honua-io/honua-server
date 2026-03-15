using Honua.Validation.Runner.Contracts;

namespace Honua.Validation.Runner.Tests;

public sealed class ValidationCatalogTests
{
    [Fact]
    public void All_ContainsExpectedTargets()
    {
        var keys = ValidationCatalog.All.Select(item => item.Key).ToArray();

        Assert.Equal(
            ["aws-ecs", "aws-lambda", "aws-eks", "azure-aca", "azure-functions", "azure-aks"],
            keys);
    }

    [Fact]
    public void All_TargetKeysAreUnique()
    {
        var keys = ValidationCatalog.All.Select(item => item.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
