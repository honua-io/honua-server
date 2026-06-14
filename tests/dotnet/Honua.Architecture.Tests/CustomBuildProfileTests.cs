// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
        var serverProjectPath = Path.Combine(repositoryRoot, "src", "Honua.Server", "Honua.Server.csproj");
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
            reference.Include == @"..\Honua.Aws\Honua.Aws.csproj" &&
            reference.Condition == "'$(HonuaIncludeAws)' == 'true'");
        projectReferences.Should().Contain(reference =>
            reference.Include == @"..\Honua.Azure\Honua.Azure.csproj" &&
            reference.Condition == "'$(HonuaIncludeAzure)' == 'true'");
        projectReferences.Should().Contain(reference =>
            reference.Include == @"..\Honua.Oracle\Honua.Oracle.csproj" &&
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
    public void Dockerfiles_ShouldExposeAndForwardCustomBuildProfileArguments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfiles = new[]
        {
            Path.Combine(repositoryRoot, "Dockerfile"),
            Path.Combine(repositoryRoot, "docker", "Dockerfile.aot")
        };

        foreach (var dockerfile in dockerfiles)
        {
            var contents = File.ReadAllText(dockerfile);

            contents.Should().Contain("ARG HONUA_BUILD_PROFILE=full", dockerfile);
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
        var source = File.ReadAllText(Path.Combine(
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
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(Path.Combine("src", "Honua.Server", "Honua.Server.csproj"));
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

        process.ExitCode.Should().Be(
            0,
            "dotnet msbuild must evaluate custom build profiles successfully. stderr: {0}",
            error);

        return output;
    }
}
