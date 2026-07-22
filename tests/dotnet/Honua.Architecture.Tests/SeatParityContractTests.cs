// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Server;

namespace Honua.Architecture.Tests;

/// <summary>
/// Architecture contracts for the console/agent seat-parity promise tracked by #2567.
/// </summary>
public sealed class SeatParityContractTests
{
    private const string OpsParityMapRelativePath =
        "tests/dotnet/Honua.Ai.Tests/ConformanceSchemas/geospatial-mcp/ops-parity-map.yaml";

    private static readonly Regex RouteLineRegex = new(
        "^\\s{2}\"(?<route>[^\"]+)\"\\s*:\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RoutePropertyRegex = new(
        "^\\s{4}(?<key>tool|resource|human-only)\\s*:\\s*(?<value>.+?)\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OperationKindRegex = new(
        "Kind\\s*=\\s*OperationClass\\.(?<kind>\\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExecutorRegistrationRegex = new(
        "(?:TryAddSingleton|AddSingleton)<\\s*Honua\\.Core\\.Features\\.ControlPlane\\.Abstractions\\.IOperationExecutor\\s*,\\s*(?<type>[\\w\\.]+)\\s*>\\s*\\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AdminConfigApplierRegistrationRegex = new(
        "(?:TryAddSingleton|AddSingleton)<\\s*Honua\\.Core\\.Features\\.ControlPlane\\.Abstractions\\.IAdminConfigChangeApplier\\s*,\\s*(?<type>[\\w\\.]+)\\s*>\\s*\\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExecutorKindSourceRegex = new(
        "OperationClass\\s*=>\\s*OperationClass\\.(?<kind>\\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OpsActionPayloadBuilderRegex = new(
        "OpsActionExecutionPayloads\\.(?<method>\\w+)\\s*\\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void OpsParityMap_CoversOperateObservabilityAutonomyDeployAndProposalRoutes()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var parityMap = LoadParityMap(repoRoot);
        var expectedRoutes = EndpointRegistry.All
            .Where(IsOpsParityRoute)
            .Select(endpoint => $"{endpoint.Method.ToUpperInvariant()} {endpoint.Path}")
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        parityMap.Keys.OrderBy(route => route, StringComparer.Ordinal)
            .Should().BeEquivalentTo(
                expectedRoutes,
                "every operate/admin-observability/autonomy/deploy/proposal EndpointRegistry route must be mapped for MCP parity");

        var implementedMcpTools = LoadImplementedMcpTools(repoRoot);
        foreach (var (route, entry) in parityMap)
        {
            var populatedFields = new[]
                {
                    entry.Tool,
                    entry.Resource,
                    entry.HumanOnly
                }
                .Count(value => !string.IsNullOrWhiteSpace(value));

            populatedFields.Should().Be(
                1,
                $"{route} must choose exactly one parity target: tool, resource, or human-only");

            if (!string.IsNullOrWhiteSpace(entry.Tool))
            {
                implementedMcpTools.Should().Contain(
                    entry.Tool,
                    $"{route} maps to a checked-in implemented geospatial-mcp tool");
            }

            if (!string.IsNullOrWhiteSpace(entry.Resource))
            {
                entry.Resource.Should().StartWith("honua://", $"{route} maps to an MCP resource URI");
            }

            if (!string.IsNullOrWhiteSpace(entry.HumanOnly))
            {
                entry.HumanOnly.Should().NotBeNullOrWhiteSpace(
                    $"{route} is operator-only and must carry an explicit justification");
            }
        }
    }

    [ArchitectureTest]
    public void OpsFindingRecommendedActions_RouteThroughRealGatewayExecutors()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var findingsSource = ReadRepoFile(repoRoot, "src/Honua.Server/Features/Infrastructure/Monitoring/OpsFindingsService.cs");
        var programSource = ReadRepoFile(repoRoot, "src/Honua.Server/Program.cs");

        var recommendedKinds = OperationKindRegex.Matches(findingsSource)
            .Select(match => match.Groups["kind"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();

        recommendedKinds.Should().NotBeEmpty(
            "ops findings with recommended actions must be checked against the gateway executor surface");

        var registeredExecutorKinds = ExtractRegisteredExecutorKinds(repoRoot, programSource);
        recommendedKinds.Should().OnlyContain(
            kind => registeredExecutorKinds.Contains(kind),
            "every recommended action kind emitted by OpsFindingsService must have a registered IOperationExecutor");

        var registeredAppliers = AdminConfigApplierRegistrationRegex.Matches(programSource)
            .Select(match => match.Groups["type"].Value)
            .ToArray();

        registeredAppliers.Should().Contain(
            "Honua.ControlPlane.Executors.OpsActionAdminConfigChangeApplier",
            "AdminConfigChange findings must execute through the real ops-action applier");
        registeredAppliers.Should().NotContain(
            type => type.EndsWith(".LoggingAdminConfigChangeApplier", StringComparison.Ordinal)
                || string.Equals(type, "LoggingAdminConfigChangeApplier", StringComparison.Ordinal),
            "a logging/no-op admin config applier silently drops approved actions");

        var loggingAppliers = ArchitectureTestHelpers.GetTypesSafely(typeof(EndpointRegistry).Assembly)
            .Where(type => typeof(IAdminConfigChangeApplier).IsAssignableFrom(type))
            .Where(type => type.Name.Contains("Logging", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToArray();

        loggingAppliers.Should().BeEmpty(
            "production IAdminConfigChangeApplier implementations must not be logging/no-op stubs");
    }

    [ArchitectureTest]
    public void OpsFindingAdminConfigActions_AreRegisteredInOpsActionCatalog()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var findingsSource = ReadRepoFile(repoRoot, "src/Honua.Server/Features/Infrastructure/Monitoring/OpsFindingsService.cs");
        var actionModelSource = ReadRepoFile(repoRoot, "src/Honua.Server/Features/ControlPlane/Executors/OpsActionModel.cs");

        var adminConfigActionContexts = OperationKindRegex.Matches(findingsSource)
            .Where(match => string.Equals(match.Groups["kind"].Value, "AdminConfigChange", StringComparison.Ordinal))
            .Select(match => findingsSource.Substring(
                match.Index,
                Math.Min(800, findingsSource.Length - match.Index)))
            .ToArray();

        adminConfigActionContexts.Should().NotBeEmpty(
            "registry-dispatched AdminConfigChange findings should exist and be validated");

        foreach (var context in adminConfigActionContexts)
        {
            context.Should().Contain(
                "ExecutionPayload = OpsActionExecutionPayloads.",
                "AdminConfigChange findings must use the typed ops-action payload builders instead of hand-rolled payloads");
        }

        var builderMethods = adminConfigActionContexts
            .SelectMany(context => OpsActionPayloadBuilderRegex.Matches(context)
                .Select(match => match.Groups["method"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(method => method, StringComparer.Ordinal)
            .ToArray();

        var actionNamesByField = LoadOpsActionNameConstants();
        var registeredActions = LoadRegisteredOpsActions();
        var emittedActions = builderMethods
            .Select(method => ExtractOpsActionNameFromBuilder(actionModelSource, method))
            .Select(field => actionNamesByField[field])
            .OrderBy(action => action, StringComparer.Ordinal)
            .ToArray();

        emittedActions.Should().OnlyContain(
            action => registeredActions.Contains(action),
            "every ops-action payload emitted by a finding must exist in the T4 ops-action registry");
    }

    private static bool IsOpsParityRoute(EndpointDefinition endpoint)
        => endpoint.Path.StartsWith("/api/v1/operate/", StringComparison.Ordinal)
            || endpoint.Path.StartsWith("/api/v1/admin/observability/ops-health", StringComparison.Ordinal)
            || endpoint.Path.StartsWith("/api/v1/admin/observability/findings", StringComparison.Ordinal)
            || endpoint.Path.StartsWith("/api/v1/admin/observability/autonomy/", StringComparison.Ordinal)
            || endpoint.Path.StartsWith("/api/v1/admin/deploy/", StringComparison.Ordinal)
            || endpoint.Path.StartsWith("/api/v1/admin/proposals", StringComparison.Ordinal)
            || string.Equals(endpoint.Path, "/api/v1/admin/platform-release/converge", StringComparison.Ordinal);

    private static Dictionary<string, ParityMapEntry> LoadParityMap(string repoRoot)
    {
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, OpsParityMapRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"the #2567 parity map should exist at {path}");

        var entries = new Dictionary<string, ParityMapEntry>(StringComparer.Ordinal);
        string? currentRoute = null;
        ParityMapEntry currentEntry = default;

        foreach (var rawLine in File.ReadLines(path))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || string.Equals(trimmed, "routes:", StringComparison.Ordinal))
            {
                continue;
            }

            var routeMatch = RouteLineRegex.Match(rawLine);
            if (routeMatch.Success)
            {
                AddCurrentEntry(entries, currentRoute, currentEntry);
                currentRoute = routeMatch.Groups["route"].Value;
                entries.Should().NotContainKey(currentRoute, $"route '{currentRoute}' should appear once in the parity map");
                currentEntry = default;
                continue;
            }

            var propertyMatch = RoutePropertyRegex.Match(rawLine);
            propertyMatch.Success.Should().BeTrue($"line '{rawLine}' must be a route key or route property");
            // Intentional: FluentAssertions' Should() extension is null-safe (extension methods
            // never NRE on a null receiver), so this *is* the explicit null guard for currentRoute,
            // not an unguarded dereference.
            // codeql[cs/dereferenced-value-may-be-null] -- the preceding assertion or validation establishes non-nullness for this access.
            currentRoute.Should().NotBeNull($"line '{rawLine}' must belong to a route entry");

            var value = Unquote(propertyMatch.Groups["value"].Value.Trim());
            currentEntry = propertyMatch.Groups["key"].Value switch
            {
                "tool" => currentEntry with { Tool = value },
                "resource" => currentEntry with { Resource = value },
                "human-only" => currentEntry with { HumanOnly = value },
                _ => currentEntry
            };
        }

        AddCurrentEntry(entries, currentRoute, currentEntry);
        return entries;
    }

    private static void AddCurrentEntry(
        IDictionary<string, ParityMapEntry> entries,
        string? currentRoute,
        ParityMapEntry currentEntry)
    {
        if (currentRoute is not null)
        {
            entries.Add(currentRoute, currentEntry);
        }
    }

    private static HashSet<string> LoadImplementedMcpTools(string repoRoot)
    {
        var indexPath = ArchitectureTestHelpers.CombinePath(
            repoRoot,
            "tests",
            "dotnet",
            "Honua.Ai.Tests",
            "ConformanceSchemas",
            "geospatial-mcp",
            "index.json");

        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        return document.RootElement
            .GetProperty("tools")
            .EnumerateArray()
            .Where(tool => string.Equals(tool.GetProperty("implementationStatus").GetString(), "implemented", StringComparison.Ordinal))
            .Select(tool => tool.GetProperty("referenceToolName").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractRegisteredExecutorKinds(string repoRoot, string programSource)
    {
        var executorTypes = ExecutorRegistrationRegex.Matches(programSource)
            .Select(match => match.Groups["type"].Value)
            .ToArray();

        executorTypes.Should().NotBeEmpty("Program.cs must register gateway executors");

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var executorType in executorTypes)
        {
            var source = FindTypeSource(repoRoot, executorType);
            var match = ExecutorKindSourceRegex.Match(source);
            match.Success.Should().BeTrue($"{executorType} must declare a stable OperationClass");
            kinds.Add(match.Groups["kind"].Value);
        }

        return kinds;
    }

    private static Dictionary<string, string> LoadOpsActionNameConstants()
    {
        var type = typeof(EndpointRegistry).Assembly.GetType("Honua.ControlPlane.Executors.OpsActionNames");
        type.Should().NotBeNull("OpsActionNames is the stable action-id vocabulary");

        return type!.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .ToDictionary(
                field => field.Name,
                field => (string)field.GetValue(null)!,
                StringComparer.Ordinal);
    }

    private static HashSet<string> LoadRegisteredOpsActions()
    {
        var type = typeof(EndpointRegistry).Assembly.GetType("Honua.ControlPlane.Executors.OpsActionCatalog");
        type.Should().NotBeNull("OpsActionCatalog is the T4 action registry");

        var field = type!.GetField("GuardrailTiers", BindingFlags.Public | BindingFlags.Static);
        field.Should().NotBeNull("OpsActionCatalog.GuardrailTiers is the registered action-id set");

        var values = (IEnumerable)field!.GetValue(null)!;
        return values
            .Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ExtractOpsActionNameFromBuilder(string actionModelSource, string methodName)
    {
        var regex = new Regex(
            "\\bpublic\\s+static\\s+string\\s+" + Regex.Escape(methodName)
            + "\\s*\\([^)]*\\)\\s*=>\\s*Build\\(OpsActionNames\\.(?<name>\\w+)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        var match = regex.Match(actionModelSource);
        match.Success.Should().BeTrue($"OpsActionExecutionPayloads.{methodName} must delegate to a registered OpsActionNames constant");
        return match.Groups["name"].Value;
    }

    private static string FindTypeSource(string repoRoot, string fullTypeName)
    {
        var typeName = fullTypeName[(fullTypeName.LastIndexOf('.') + 1)..];
        // Registered executors may live in any src assembly (e.g. the geoprocessing executor lives in
        // Honua.Geoprocessing, not Honua.Server), so search the whole src tree for the type source.
        var serverRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");
        var source = Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .FirstOrDefault(source => source.Contains("class " + typeName, StringComparison.Ordinal)
                || source.Contains("record " + typeName, StringComparison.Ordinal));

        if (source is null)
        {
            throw new FileNotFoundException($"Unable to locate source for {fullTypeName}.");
        }

        return source;
    }

    private static string ReadRepoFile(string repoRoot, string relativePath)
        => File.ReadAllText(ArchitectureTestHelpers.CombinePath(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private readonly record struct ParityMapEntry(string? Tool, string? Resource, string? HumanOnly);
}
