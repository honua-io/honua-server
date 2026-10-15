// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Honua.Architecture.Tests;

/// <summary>
/// The four build-time protocol profiles select the same projects, constants, and receipt.
/// </summary>
public sealed class ProtocolProfileTests
{
    private static readonly string[] Profiles = ["gsr", "ogc-api", "ogc-api-classic", "all"];

    [ArchitectureTest]
    public void Definition_ShouldListExactlyTheFourProfilesAndDefaultToAll()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DefinitionPath()));
        var root = document.RootElement;
        root.GetProperty("defaultProfile").GetString().Should().Be("all");
        root.GetProperty("supportedProfiles").EnumerateArray().Select(item => item.GetString())
            .Should().Equal(Profiles);
        root.GetProperty("profiles").EnumerateObject().Select(property => property.Name)
            .Should().Equal(Profiles);
    }

    [ArchitectureTest]
    public void EachProfile_ShouldSelectIncludesConstantsAndProjectReferences()
    {
        AssertProfile(
            "gsr",
            included: ["Honua.Protocols.GeoServices"],
            excluded:
            [
                "Honua.Protocols.OgcApi", "Honua.Protocols.OgcClassic", "Honua.Protocols.OData",
                "Honua.Protocols.Stac", "Honua.Protocols.SensorThings", "Honua.Protocols.Scene", "Honua.Scene"
            ],
            constants: ["HONUA_PROTOCOL_GSR", "HONUA_EXCLUDE_OGC_API", "HONUA_EXCLUDE_ODATA", "HONUA_EXCLUDE_GRPC", "HONUA_EXCLUDE_MCP"],
            absentConstants: ["HONUA_EXCLUDE_GEOSERVICES"]);
        AssertProfile(
            "ogc-api",
            included: ["Honua.Protocols.OgcApi", "Honua.Protocols.Ogc.Shared"],
            excluded: ["Honua.Protocols.GeoServices", "Honua.Protocols.OgcClassic", "Honua.Protocols.OData", "Honua.Scene"],
            constants: ["HONUA_PROTOCOL_OGC_API", "HONUA_EXCLUDE_GEOSERVICES", "HONUA_EXCLUDE_OGC_CLASSIC"],
            absentConstants: ["HONUA_EXCLUDE_OGC_API"]);
        AssertProfile(
            "ogc-api-classic",
            included: ["Honua.Protocols.OgcApi", "Honua.Protocols.OgcClassic"],
            excluded: ["Honua.Protocols.GeoServices", "Honua.Protocols.OData", "Honua.Protocols.Stac"],
            constants: ["HONUA_PROTOCOL_OGC_API_CLASSIC", "HONUA_EXCLUDE_GEOSERVICES", "HONUA_EXCLUDE_STAC"],
            absentConstants: ["HONUA_EXCLUDE_OGC_API", "HONUA_EXCLUDE_OGC_CLASSIC"]);
        AssertProfile(
            "all",
            included:
            [
                "Honua.Protocols.GeoServices", "Honua.Protocols.OgcApi", "Honua.Protocols.OgcClassic",
                "Honua.Protocols.OData", "Honua.Protocols.Stac", "Honua.Protocols.SensorThings",
                "Honua.Protocols.Scene", "Honua.Scene"
            ],
            excluded: [],
            constants: ["HONUA_PROTOCOL_ALL"],
            absentConstants: ["HONUA_EXCLUDE_GEOSERVICES", "HONUA_EXCLUDE_OGC_API", "HONUA_EXCLUDE_ODATA", "HONUA_EXCLUDE_MCP"]);
    }

    [ArchitectureTest]
    public void EveryProfile_ShouldKeepSharedDependencies()
    {
        foreach (var profile in Profiles)
        {
            var names = GetProjectReferenceNames($"-p:HonuaProtocolProfile={profile}");
            names.Should().Contain("Honua.Routing");
            names.Should().Contain("Honua.Protocols.Ogc.Shared");
            names.Should().Contain("Honua.Geoprocessing");
            names.Should().Contain("Honua.Ai");
            names.Should().Contain("Honua.GeoServicesRest");
        }
    }

    [ArchitectureTest]
    public void UnknownProfile_ShouldFailAndNameSupportedProfiles()
    {
        var output = RunMsBuildExpectingFailure(
            "-t:ValidateHonuaProtocolProfile",
            "-p:HonuaProtocolProfile=nope");
        output.Should().Contain("Supported profiles: gsr, ogc-api, ogc-api-classic, all.");
    }

    [ArchitectureTest]
    public void InconsistentModuleOverride_ShouldFail()
    {
        var output = RunMsBuildExpectingFailure(
            "-t:ValidateHonuaProtocolProfile",
            "-p:HonuaProtocolProfile=gsr",
            "-p:HonuaIncludeOData=true");
        output.Should().Contain("does not allow module overrides");
        output.Should().Contain("gsr");
    }

    [ArchitectureTest]
    public void ProviderProfile_ShouldStayIndependentOfProtocolProfile()
    {
        var properties = GetMsBuildProperties(
            "HonuaIncludeAws,HonuaIncludeGeoServices",
            "-p:HonuaProtocolProfile=all",
            "-p:HonuaBuildProfile=no-cloud");
        properties["HonuaIncludeAws"].Should().Be("false");
        properties["HonuaIncludeGeoServices"].Should().Be("true");

        var constants = GetMsBuildProperty(
                "DefineConstants",
                "-p:HonuaProtocolProfile=all",
                "-p:HonuaBuildProfile=no-cloud")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        constants.Should().Contain("HONUA_EXCLUDE_AWS");
        constants.Should().NotContain("HONUA_EXCLUDE_GEOSERVICES");
    }

    [ArchitectureTest]
    public void NonDefaultProfile_ShouldIsolateOutputAndDefaultShouldNot()
    {
        GetMsBuildProperty("OutputPath", "-p:HonuaProtocolProfile=gsr")
            .Should().Contain("protocol-gsr");
        GetMsBuildProperty("OutputPath", "-p:HonuaProtocolProfile=all")
            .Should().NotContain("protocol-");
    }

    [ArchitectureTest]
    public void Dockerfiles_ShouldForwardTheProtocolProfile()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var relative in new[] { "Dockerfile", Path.Combine("docker", "Dockerfile.aot") })
        {
            var contents = File.ReadAllText(Path.Combine(repositoryRoot, relative));
            contents.Should().Contain("ARG HONUA_PROTOCOL_PROFILE=all");
            contents.Should().Contain("COPY eng/Honua.ProtocolProfiles.props eng/");
            contents.Should().Contain("-p:HonuaProtocolProfile=${HONUA_PROTOCOL_PROFILE:-all}");
        }

        var shim = string.Join('\n', File.ReadLines(Path.Combine(repositoryRoot, "docker", "Dockerfile.protocol-profile-runtime"))
            .Where(line => !line.TrimStart().StartsWith('#')));
        shim.Should().Contain("honua-protocol-profile.json");
        shim.Should().NotContain("dotnet publish");
        shim.Should().NotContain("dotnet build");
        shim.Should().NotContain("security.read-only-root");
    }

    [ArchitectureTest]
    public void Registration_ShouldGateProtocolSurfacesWithExcludeSymbols()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Honua.Server",
            "Features",
            "Infrastructure",
            "Hosting",
            "FeatureRegistrationExtensions.cs"));
        source.Should().Contain("#if !HONUA_EXCLUDE_GEOSERVICES");
        source.Should().Contain("#if !HONUA_EXCLUDE_OGC_API");
        source.Should().Contain("#if !HONUA_EXCLUDE_OGC_CLASSIC");
        source.Should().Contain("#if !HONUA_EXCLUDE_ODATA");
        source.Should().Contain("#if !HONUA_EXCLUDE_STAC");
        source.Should().Contain("#if !HONUA_EXCLUDE_SENSORTHINGS");
        source.Should().Contain("#if !HONUA_EXCLUDE_SCENE");
        source.Should().Contain("#if !HONUA_EXCLUDE_GRPC");
        source.Should().Contain("#if !HONUA_EXCLUDE_MCP");
        source.Should().NotContain("services.AddDiscoveredProtocolModules(");
        source.Should().NotContain("endpoints.MapDiscoveredProtocolModules(");
    }

    [ArchitectureTest]
    public void CapabilityPrefixes_ShouldMatchTheCompiledProfileRules()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DefinitionPath()));
        var source = File.ReadAllText(Path.Combine(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            "src",
            "Honua.Server",
            "Features",
            "Infrastructure",
            "Hosting",
            "ProtocolProfile.cs"));
        foreach (var module in document.RootElement.GetProperty("modules").EnumerateObject())
        {
            foreach (var prefix in module.Value.GetProperty("capabilityPrefixes").EnumerateArray())
            {
                source.Should().Contain(prefix.GetString());
            }
        }
    }

    [ArchitectureTest]
    public void CompiledDefault_ShouldBeAll()
    {
        ProtocolProfile.Name.Should().Be("all");
        ProtocolProfile.IncludesGeoServices.Should().BeTrue();
        ProtocolProfile.IncludesOgcApi.Should().BeTrue();
        ProtocolProfile.IncludesOgcClassic.Should().BeTrue();
        ProtocolProfile.IncludesOData.Should().BeTrue();
        ProtocolProfile.IncludesStac.Should().BeTrue();
        ProtocolProfile.IncludesSensorThings.Should().BeTrue();
        ProtocolProfile.IncludesScene.Should().BeTrue();
        ProtocolProfile.IncludesGrpc.Should().BeTrue();
        ProtocolProfile.IncludesMcp.Should().BeTrue();
        ProtocolProfile.IsCapabilityCompiled("serve.geoservices-featureserver").Should().BeTrue();
        ProtocolProfile.IsCapabilityCompiled("query.features").Should().BeTrue();
    }

    [ArchitectureTest]
    public void RuntimeRequest_ShouldRejectADifferentProfileAndUnknownModule()
    {
        ProtocolProfile.ValidateRuntime(Config());
        ProtocolProfile.ValidateRuntime(Config(("HONUA_PROTOCOL_PROFILE", "all")));
        ProtocolProfile.ValidateRuntime(Config(("Protocols:Enabled", "odata,geoservices")));

        var mismatch = () => ProtocolProfile.ValidateRuntime(Config(("HONUA_PROTOCOL_PROFILE", "gsr")));
        mismatch.Should().Throw<InvalidOperationException>()
            .WithMessage("*HONUA_PROTOCOL_PROFILE is 'gsr'*compiled profile 'all'*HonuaProtocolProfile=gsr*");

        var unknown = () => ProtocolProfile.ValidateRuntime(Config(("Protocols:Enabled", "not-a-protocol")));
        unknown.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a protocol module*");
    }

    [ArchitectureTest]
    public void Receipt_ShouldRejectADifferentProfile()
    {
        const string gsr = """{"format":"honua.protocol-profile/v1","profile":"gsr","buildProfile":"full","modules":["geoservices"],"excluded":[],"retainedShared":[]}""";
        var act = () => ProtocolProfileReceipt.Check("all", gsr);
        act.Should().Throw<InvalidOperationException>();
        ProtocolProfileReceipt.Check("gsr", gsr);
        var mismatchedModules = """{"format":"honua.protocol-profile/v1","profile":"gsr","buildProfile":"full","modules":["odata"],"excluded":[],"retainedShared":[]}""";
        var modules = () => ProtocolProfileReceipt.Check("gsr", mismatchedModules);
        modules.Should().Throw<InvalidOperationException>();
    }

    private static MapConfiguration Config(params (string Key, string? Value)[] values)
        => new MapConfiguration(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private sealed class MapConfiguration : IConfigurationSection
    {
        private readonly Dictionary<string, string?> _data;
        private readonly string _path;

        public MapConfiguration(Dictionary<string, string?> data, string path = "")
        {
            _data = data;
            _path = path;
        }

        public string Key => _path.Contains(':', StringComparison.Ordinal)
            ? _path[(_path.LastIndexOf(':') + 1)..]
            : _path;

        public string Path => _path;

        public string? Value
        {
            get => _path.Length == 0 ? null : (_data.TryGetValue(_path, out var value) ? value : null);
            set => _data[_path] = value;
        }

        public string? this[string key]
        {
            get => _data.TryGetValue(Full(key), out var value) ? value : null;
            set => _data[Full(key)] = value;
        }

        public IConfigurationSection GetSection(string key) => new MapConfiguration(_data, Full(key));

        public IEnumerable<IConfigurationSection> GetChildren()
        {
            var prefix = _path.Length == 0 ? string.Empty : _path + ":";
            var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _data.Keys)
            {
                if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rest = entry[prefix.Length..];
                var separator = rest.IndexOf(':');
                var child = separator < 0 ? rest : rest[..separator];
                if (child.Length > 0)
                {
                    children.Add(child);
                }
            }

            foreach (var child in children)
            {
                yield return GetSection(child);
            }
        }

        public IChangeToken GetReloadToken() => NoChange.Instance;

        private string Full(string key) => _path.Length == 0 ? key : _path + ":" + key;
    }

    private sealed class NoChange : IChangeToken
    {
        public static readonly NoChange Instance = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => Empty.Instance;

        private sealed class Empty : IDisposable
        {
            public static readonly Empty Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private static void AssertProfile(
        string profile,
        string[] included,
        string[] excluded,
        string[] constants,
        string[] absentConstants)
    {
        var argument = $"-p:HonuaProtocolProfile={profile}";
        var names = GetProjectReferenceNames(argument);
        names.Should().Contain(included);
        names.Intersect(excluded).Should().BeEmpty();
        var defined = GetMsBuildProperty("DefineConstants", argument)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        defined.Should().Contain(constants);
        defined.Intersect(absentConstants).Should().BeEmpty();
    }

    private static string DefinitionPath()
        => Path.Combine(ArchitectureTestHelpers.ResolveRepositoryRoot(), "eng", "protocol-profiles.v1.json");

    private static Dictionary<string, string> GetMsBuildProperties(string propertyNames, params string[] msbuildArguments)
    {
        using var document = JsonDocument.Parse(RunMsBuild($"-getProperty:{propertyNames}", msbuildArguments));
        return document.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static string GetMsBuildProperty(string propertyName, params string[] msbuildArguments)
        => RunMsBuild($"-getProperty:{propertyName}", msbuildArguments).Trim().Trim('"');

    private static List<string> GetProjectReferenceNames(params string[] msbuildArguments)
    {
        using var document = JsonDocument.Parse(RunMsBuild("-getItem:ProjectReference", msbuildArguments));
        return document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
            .Select(element => element.GetProperty("Filename").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static string RunMsBuild(string queryArgument, params string[] msbuildArguments)
    {
        var (exitCode, output, error) = ExecuteMsBuild(queryArgument, msbuildArguments);
        exitCode.Should().Be(0, "dotnet msbuild must evaluate protocol profiles. stderr: {0}", error);
        return output;
    }

    private static string RunMsBuildExpectingFailure(params string[] msbuildArguments)
    {
        var (exitCode, output, error) = ExecuteMsBuild(null, msbuildArguments);
        exitCode.Should().NotBe(0, "dotnet msbuild must reject this protocol profile. stdout: {0}", output);
        return error + output;
    }

    private static (int ExitCode, string Output, string Error) ExecuteMsBuild(
        string? queryArgument,
        params string[] msbuildArguments)
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
        if (queryArgument is not null)
        {
            startInfo.ArgumentList.Add(queryArgument);
        }

        foreach (var argument in msbuildArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet msbuild.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(milliseconds: 90_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet msbuild property evaluation timed out.");
        }

        return (process.ExitCode, output, error);
    }
}
