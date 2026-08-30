// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed partial class DeployBackendRollbackTruthfulnessTests
{
    private static readonly string[] AutomaticRollbackBackends =
    [
        "KubernetesArgoRolloutsDeployBackend",
        "AwsEcsAlbDeployBackend",
        "AzureContainerAppsRevisionDeployBackend",
        "AwsLambdaGitOpsDeployBackend",
        "AzureFunctionsGitOpsDeployBackend",
        "YarpRollingDeployBackend"
    ];

    private static readonly string[] ManualHandoffBackends =
    [
        "KubernetesGitOpsDeployBackend",
        "AwsEcsGitOpsDeployBackend",
        "AzureContainerAppsGitOpsDeployBackend"
    ];

    [Fact]
    public void RegisteredDeployBackends_AdvertiseRollbackOnlyWithTerminalObservationPath()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var registration = File.ReadAllText(Path.Combine(
            root, "src", "Honua.Server", "Startup", "BatchAndDeployBackendsRegistration.cs"));
        var registered = RegisteredBackendRegex()
            .Matches(registration)
            .Select(match => match.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var classified = AutomaticRollbackBackends
            .Concat(ManualHandoffBackends)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(classified.Order(StringComparer.Ordinal), registered.Order(StringComparer.Ordinal));

        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        foreach (var backend in AutomaticRollbackBackends)
        {
            var body = FindTypeBody(sources, backend);
            var observationPath = FindMethodBody(body, "ObserveAsync");
            if (backend == "KubernetesArgoRolloutsDeployBackend")
            {
                observationPath += FindMethodBody(body, "ObserveRollback");
            }

            Assert.Contains("SupportsRollback = true", body, StringComparison.Ordinal);
            Assert.Contains("RollbackAsync", body, StringComparison.Ordinal);
            Assert.Contains("Status = WorkflowOperationStatus.RolledBack", observationPath, StringComparison.Ordinal);
        }

        var gitOpsBase = FindTypeBody(sources, "GitOpsDeployBackendBase");
        Assert.Contains("SupportsRollback = false", gitOpsBase, StringComparison.Ordinal);
        Assert.Contains("Status = WorkflowOperationStatus.ManualInterventionRequired", gitOpsBase, StringComparison.Ordinal);

        foreach (var backend in ManualHandoffBackends)
        {
            var declaration = FindTypeDeclaration(sources, backend);
            Assert.Contains(": GitOpsDeployBackendBase", declaration, StringComparison.Ordinal);
        }
    }

    private static string FindTypeDeclaration(IEnumerable<string> sources, string typeName)
    {
        var marker = $"class {typeName}";
        foreach (var source in sources)
        {
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var openingBrace = source.IndexOf('{', start);
            Assert.True(openingBrace >= 0, $"Type '{typeName}' has no body.");
            return source[start..openingBrace];
        }

        throw new Xunit.Sdk.XunitException($"Registered deploy backend type '{typeName}' was not found in src.");
    }

    private static string FindTypeBody(IEnumerable<string> sources, string typeName)
    {
        var marker = $"class {typeName}";
        foreach (var source in sources)
        {
            var declaration = source.IndexOf(marker, StringComparison.Ordinal);
            if (declaration < 0)
            {
                continue;
            }

            var openingBrace = source.IndexOf('{', declaration);
            var depth = 0;
            for (var index = openingBrace; index < source.Length; index++)
            {
                depth += source[index] == '{' ? 1 : source[index] == '}' ? -1 : 0;
                if (depth == 0)
                {
                    return source[openingBrace..(index + 1)];
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Registered deploy backend type '{typeName}' was not found in src.");
    }

    private static string FindMethodBody(string typeBody, string methodName)
    {
        var declarationMatch = Regex.Match(
            typeBody,
            $@"(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[A-Za-z0-9_<>,?\[\]]+\s+{Regex.Escape(methodName)}\(",
            RegexOptions.CultureInvariant);
        Assert.True(declarationMatch.Success, $"Method '{methodName}' was not found in the backend type.");

        var openingBrace = typeBody.IndexOf('{', declarationMatch.Index);
        Assert.True(openingBrace >= 0, $"Method '{methodName}' has no body.");

        var depth = 0;
        for (var index = openingBrace; index < typeBody.Length; index++)
        {
            depth += typeBody[index] == '{' ? 1 : typeBody[index] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return typeBody[openingBrace..(index + 1)];
            }
        }

        throw new Xunit.Sdk.XunitException($"Method '{methodName}' has an unterminated body.");
    }

    [GeneratedRegex(@"GetRequiredService<(?<type>[A-Za-z0-9_]+DeployBackend)>\(\)")]
    private static partial Regex RegisteredBackendRegex();
}
