// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the custom Docker/MSBuild build-profile contract for optional modules.
/// </summary>
public sealed class CustomBuildProfileTests
{
    [ArchitectureTest]
    public void HonuaServer_ShouldImportBuildProfilesAndConditionCloudReferences()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var serverProjectPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, "src", "Honua.Server", "Honua.Server.csproj");
        var document = XDocument.Load(serverProjectPath);

        document.Descendants("Import")
            .Select(element => element.Attribute("Project")?.Value)
            .Should()
            .Contain(@"..\..\eng\Honua.BuildProfiles.props");

        var projectReferences = document.Descendants("ProjectReference")
            .Select(element => new
            {
                Include = element.Attribute("Include")?.Value,
                Condition = element.Attribute("Condition")?.Value
            })
            .ToList();

        projectReferences.Should().Contain(reference =>
            reference.Include == @"..\Honua.Cloud\Aws\Honua.Aws.csproj" &&
            reference.Condition == "'$(HonuaIncludeAws)' == 'true'");
        projectReferences.Should().Contain(reference =>
            reference.Include == @"..\Honua.Cloud\Azure\Honua.Azure.csproj" &&
            reference.Condition == "'$(HonuaIncludeAzure)' == 'true'");
        projectReferences.Should().Contain(reference =>
            reference.Include == @"..\Honua.Db\Oracle\Honua.Oracle.csproj" &&
            reference.Condition == "'$(HonuaIncludeOracle)' == 'true'");
    }

    [ArchitectureTest]
    public void NoCloudProfile_ShouldDisableAwsAndAzureWhileAllowingExplicitOverrides()
    {
        var noCloudProperties = GetMsBuildProperties(
            "HonuaIncludeAws,HonuaIncludeAzure,HonuaIncludeOracle",
            "-p:HonuaBuildProfile=no-cloud");

        noCloudProperties["HonuaIncludeAws"].Should().Be("false");
        noCloudProperties["HonuaIncludeAzure"].Should().Be("false");
        noCloudProperties["HonuaIncludeOracle"].Should().Be("true");

        var overriddenProperties = GetMsBuildProperties(
            "HonuaIncludeAws,HonuaIncludeAzure",
            "-p:HonuaBuildProfile=no-cloud",
            "-p:HonuaIncludeAws=true");

        overriddenProperties["HonuaIncludeAws"].Should().Be("true");
        overriddenProperties["HonuaIncludeAzure"].Should().Be("false");

        var slimProperties = GetMsBuildProperties(
            "HonuaIncludeAws,HonuaIncludeAzure,HonuaIncludeOracle",
            "-p:HonuaBuildProfile=slim");

        slimProperties["HonuaIncludeAws"].Should().Be("false");
        slimProperties["HonuaIncludeAzure"].Should().Be("false");
        slimProperties["HonuaIncludeOracle"].Should().Be("false");
    }

    [ArchitectureTest]
    public void NoCloudProfile_ShouldExcludeCloudProjectReferencesAndEmitCompileConstants()
    {
        var projectReferenceNames = GetProjectReferenceNames("-p:HonuaBuildProfile=no-cloud");

        projectReferenceNames.Should().NotContain("Honua.Aws");
        projectReferenceNames.Should().NotContain("Honua.Azure");
        projectReferenceNames.Should().Contain("Honua.Oracle");

        var constants = GetMsBuildProperty("DefineConstants", "-p:HonuaBuildProfile=no-cloud")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        constants.Should().Contain("HONUA_EXCLUDE_AWS");
        constants.Should().Contain("HONUA_EXCLUDE_AZURE");
        constants.Should().NotContain("HONUA_SKIP_ORACLE");
    }

    [ArchitectureTest]
    public void PostgisStoreProfile_ExcludesAdditionalStoresAndKeepsImportProjects()
    {
        var references = GetProjectReferenceNames("-p:HonuaStoreProfile=postgis");

        references.Should().Contain(["Honua.Postgres", "Honua.Import", "Honua.Io", "Honua.Geometry"]);
        references.Should().NotContain([
            "Honua.DuckDB", "Honua.SqlServer", "Honua.MySql", "Honua.Redshift",
            "Honua.Oracle", "Honua.Snowflake", "Honua.Databricks", "Honua.GeoServicesRest"]);
        GetMsBuildProperty("DefineConstants", "-p:HonuaStoreProfile=postgis")
            .Should().Contain("HONUA_POSTGIS_ONLY");

        var fullReferences = GetProjectReferenceNames("-p:HonuaStoreProfile=full");
        fullReferences.Should().Contain([
            "Honua.DuckDB", "Honua.SqlServer", "Honua.MySql", "Honua.Redshift",
            "Honua.Oracle", "Honua.Snowflake", "Honua.Databricks", "Honua.GeoServicesRest"]);
    }

    [ArchitectureTest]
    public void PostgisStoreProfile_AllModuleProfiles_DisablesOptionalStoreFlags()
    {
        foreach (var moduleProfile in new[] { "full", "no-cloud", "slim" })
        {
            var properties = GetMsBuildProperties(
                "HonuaIncludeOracle,HonuaIncludeSnowflake,DefineConstants",
                "-p:HonuaStoreProfile=postgis", $"-p:HonuaBuildProfile={moduleProfile}");

            properties["HonuaIncludeOracle"].Should().Be("false");
            properties["HonuaIncludeSnowflake"].Should().Be("false");
            properties["DefineConstants"].Should().Contain("HONUA_SKIP_ORACLE");
            properties["DefineConstants"].Should().Contain("HONUA_SKIP_SNOWFLAKE");
        }
    }

    [ArchitectureTest]
    public void PostgisStoreProfile_ExplicitAdditionalStoreFlags_RefusesValidation()
    {
        foreach (var optionalStoreFlag in new[] { "HonuaIncludeOracle", "HonuaIncludeSnowflake" })
        {
            var result = ExecuteMsBuild("-t:ValidateHonuaBuildProfile",
                "-p:HonuaStoreProfile=postgis", $"-p:{optionalStoreFlag}=true");

            result.ExitCode.Should().NotBe(0);
            (result.Output + result.Error).Should().Contain("require HonuaStoreProfile=full");
        }
    }

    [ArchitectureTest]
    public void DockerStoreProfileReferences_DeclareOrInheritTheirStageArgument()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var path in new[] { "Dockerfile", "docker/Dockerfile.aot",
                     "docker/Dockerfile.lambda", "docker/Dockerfile.lambda.aot" })
        {
            AssertStoreProfileArgumentScope(Path.Join(repositoryRoot, path));
        }
    }

    private static void AssertStoreProfileArgumentScope(string path)
    {
        var inheritedScopes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string? stage = null;
        var profileInScope = false;
        foreach (var sourceLine in File.ReadLines(path))
        {
            var line = sourceLine.Trim();
            if (line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                if (stage is not null)
                    inheritedScopes[stage] = profileInScope;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var parent = parts.Skip(1).First(part => !part.StartsWith("--", StringComparison.Ordinal));
                profileInScope = inheritedScopes.GetValueOrDefault(parent);
                stage = parts.Length >= 4 && parts[^2].Equals("AS", StringComparison.OrdinalIgnoreCase)
                    ? parts[^1] : null;
            }
            if (line.Equals("ARG HONUA_STORE_PROFILE", StringComparison.Ordinal)
                || line.StartsWith("ARG HONUA_STORE_PROFILE=", StringComparison.Ordinal))
                profileInScope = true;
            if (line.Contains("${HONUA_STORE_PROFILE", StringComparison.Ordinal)
                || line.Contains("$HONUA_STORE_PROFILE", StringComparison.Ordinal))
                profileInScope.Should().BeTrue($"{path}: {line} must receive its stage argument");
        }
    }

    [ArchitectureTest]
    public void Dockerfiles_ShouldExposeAndForwardCustomBuildProfileArguments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfiles = new[]
        {
            ArchitectureTestHelpers.CombinePath(repositoryRoot, "Dockerfile"),
            ArchitectureTestHelpers.CombinePath(repositoryRoot, "docker", "Dockerfile.aot")
        };

        foreach (var dockerfile in dockerfiles)
        {
            var contents = File.ReadAllText(dockerfile);

            contents.Should().Contain("ARG HONUA_BUILD_PROFILE=full", dockerfile);
            contents.Should().Contain("ARG HONUA_STORE_PROFILE=postgis", dockerfile);
            contents.Should().Contain("-p:HonuaStoreProfile=${HONUA_STORE_PROFILE:-postgis}", dockerfile);
            contents.Should().Contain("ARG HONUA_INCLUDE_AWS=", dockerfile);
            contents.Should().Contain("ARG HONUA_INCLUDE_AZURE=", dockerfile);
            contents.Should().Contain("ARG HONUA_INCLUDE_ORACLE=", dockerfile);
            contents.Should().Contain("COPY eng/Honua.BuildProfiles.props eng/", dockerfile);
            contents.Should().Contain("-p:HonuaBuildProfile=${HONUA_BUILD_PROFILE:-full}", dockerfile);
            contents.Should().Contain("-p:HonuaIncludeAws=$HONUA_INCLUDE_AWS", dockerfile);
            contents.Should().Contain("-p:HonuaIncludeAzure=$HONUA_INCLUDE_AZURE", dockerfile);
            contents.Should().Contain("-p:HonuaIncludeOracle=$HONUA_INCLUDE_ORACLE", dockerfile);
        }
    }

    [ArchitectureTest]
    public void AlertEditionPolicy_ShouldGateCloudChannelConfigurationBehindBuildProfileConstants()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var source = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            repositoryRoot, "src", "Honua.Server", "Features", "Alerts", "AlertEditionPolicy.cs"));

        // In no-cloud / slim builds the AWS / Azure channels are backed only by
        // UnsupportedAlertDeliverySink, so IsChannelConfigured must report them unconfigured even when
        // legacy AlertDelivery:Dispatch:* settings are present — otherwise the pipeline would dispatch to
        // (and the admin API would advertise) a channel whose every delivery fails immediately. Guard the
        // compile-time exclusion so the gate is not accidentally removed.
        source.Should().Contain("#if HONUA_EXCLUDE_AWS");
        source.Should().Contain("#if HONUA_EXCLUDE_AZURE");
    }

    private static Dictionary<string, string> GetMsBuildProperties(
        string propertyNames,
        params string[] msbuildArguments)
    {
        using var document = JsonDocument.Parse(RunMsBuild($"-getProperty:{propertyNames}", msbuildArguments));
        return document.RootElement
            .GetProperty("Properties")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static string GetMsBuildProperty(string propertyName, params string[] msbuildArguments)
        => RunMsBuild($"-getProperty:{propertyName}", msbuildArguments).Trim();

    private static List<string> GetProjectReferenceNames(params string[] msbuildArguments)
    {
        using var document = JsonDocument.Parse(RunMsBuild("-getItem:ProjectReference", msbuildArguments));
        return document.RootElement
            .GetProperty("Items")
            .GetProperty("ProjectReference")
            .EnumerateArray()
            .Select(element => element.GetProperty("Filename").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static string RunMsBuild(string queryArgument, params string[] msbuildArguments)
    {
        var result = ExecuteMsBuild(queryArgument, msbuildArguments);
        result.ExitCode.Should().Be(0,
            "dotnet msbuild must evaluate custom build profiles successfully. stderr: {0}", result.Error);
        return result.Output;
    }

    private static (int ExitCode, string Output, string Error) ExecuteMsBuild(
        string queryArgument, params string[] msbuildArguments)
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(ArchitectureTestHelpers.CombinePath("src", "Honua.Server", "Honua.Server.csproj"));
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:q");
        startInfo.ArgumentList.Add(queryArgument);

        foreach (var argument in msbuildArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet msbuild.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(milliseconds: 30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet msbuild property evaluation timed out.");
        }

        return (process.ExitCode, output, error);
    }
}
