// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
#if !HONUA_EXCLUDE_GEOSERVICES
using Honua.Protocols.GeoServices;
#endif
using Honua.Protocols.Ogc.Common;
#if !HONUA_EXCLUDE_STAC
using Honua.Protocols.Stac.Models;
#endif
using GrpcServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Admin;

// The embedded component-versions file is the same declaration the release lock and the capabilities advertisement read, so those copies cannot drift.
internal sealed class ComponentVersionsDeclaration
{
    internal const string ResourceName = "Honua.Server.release.component-versions.json";
    internal const string Format = "honua.component-versions/v1";
    internal const string Component = "honua-server";

    private static readonly Lazy<ComponentVersionsDeclaration> EmbeddedDeclaration = new(LoadEmbedded);

    private ComponentVersionsDeclaration(
        IReadOnlyDictionary<string, string> contractVersions,
        IReadOnlyDictionary<string, string> schemaVersions)
    {
        ContractVersions = contractVersions;
        SchemaVersions = schemaVersions;
    }

    public static ComponentVersionsDeclaration Embedded => EmbeddedDeclaration.Value;

    public IReadOnlyDictionary<string, string> ContractVersions { get; }

    public IReadOnlyDictionary<string, string> SchemaVersions { get; }

    internal static ComponentVersionsDeclaration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("the document is not a JSON object");
        }

        string? format = null;
        string? component = null;
        SortedDictionary<string, string>? contractVersions = null;
        SortedDictionary<string, string>? schemaVersions = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Invalid($"field '{property.Name}' is declared more than once");
            }

            switch (property.Name)
            {
                case "format":
                    format = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                case "component":
                    component = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                case "contractVersions":
                    contractVersions = ReadVersionMap(property.Value, property.Name);
                    break;
                case "schemaVersions":
                    schemaVersions = ReadVersionMap(property.Value, property.Name);
                    break;
                default:
                    throw Invalid($"unknown field '{property.Name}'");
            }
        }

        if (!string.Equals(format, Format, StringComparison.Ordinal))
        {
            throw Invalid($"format must be '{Format}'");
        }

        if (!string.Equals(component, Component, StringComparison.Ordinal))
        {
            throw Invalid($"component must be '{Component}'");
        }

        if (contractVersions is null || schemaVersions is null)
        {
            throw Invalid("contractVersions and schemaVersions are required");
        }

        if (schemaVersions.ContainsKey("database"))
        {
            throw Invalid("schemaVersions.database is derived from the migrations by the release resolver and must not be declared");
        }

        return new ComponentVersionsDeclaration(contractVersions, schemaVersions);
    }

    private static ComponentVersionsDeclaration LoadEmbedded()
    {
        using var stream = typeof(ComponentVersionsDeclaration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded component-version declaration '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static SortedDictionary<string, string> ReadVersionMap(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{field} must be an object mapping names to version strings");
        }

        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in element.EnumerateObject())
        {
            var version = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(version) ||
                !string.Equals(version, version.Trim(), StringComparison.Ordinal))
            {
                throw Invalid($"{field}.{entry.Name} must be a non-blank exact version string");
            }

            if (!versions.TryAdd(entry.Name, version))
            {
                throw Invalid($"{field}.{entry.Name} is declared more than once");
            }
        }

        return versions;
    }

    private static InvalidOperationException Invalid(string reason)
        => new($"eng/release/component-versions.v1.json is invalid: {reason}.");
}

internal static class ServedContractVersions
{
#if !HONUA_EXCLUDE_GRPC
    // The gRPC services Program maps (feature, process, spec, scene, tile, elevation).
    private static readonly GrpcServiceDescriptor[] GrpcServices =
    [
        Proto.FeatureService.Descriptor,
        Proto.ProcessService.Descriptor,
        Proto.SpecService.Descriptor,
        Proto.SceneService.Descriptor,
        Proto.TileService.Descriptor,
        Proto.ElevationService.Descriptor
    ];
#endif

    public static IReadOnlyDictionary<string, string> Current { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["admin"] = AdminInfoEndpoints.AdminApiMajor,
        ["metadata"] = MetadataV2Constants.ApiVersion,
#if !HONUA_EXCLUDE_GEOSERVICES
        ["geoservices"] = GeoServicesContract.Version,
#endif
#if !(HONUA_EXCLUDE_OGC_API && HONUA_EXCLUDE_OGC_CLASSIC)
        ["ogc"] = OgcContract.Version,
#endif
#if !HONUA_EXCLUDE_STAC
        ["stac"] = StacContract.Version,
#endif
#if !HONUA_EXCLUDE_GRPC
        ["grpc"] = GrpcPackageMajor(GrpcServices)
#endif
    };

    internal static string GrpcPackageMajor(IReadOnlyList<GrpcServiceDescriptor> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var packages = services.Select(static service => service.File.Package).Distinct(StringComparer.Ordinal).ToArray();
        if (packages.Length != 1)
        {
            throw new InvalidOperationException(
                $"The mapped gRPC services span {packages.Length} proto packages ({string.Join(", ", packages)}); " +
                "the declared grpc contract version can name only one.");
        }

        var package = packages[0];
        var lastDot = package.LastIndexOf('.');
        var major = lastDot >= 0 ? package[(lastDot + 1)..] : package;
        if (major.Length < 2 || major[0] != 'v' || !major[1..].All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException(
                $"The gRPC proto package '{package}' does not end in a version segment such as 'v1'.");
        }

        return major;
    }
}

internal static class ContractVersionsStartupCheck
{
    internal static void Validate(
        IReadOnlyDictionary<string, string> declared,
        IReadOnlyDictionary<string, string> served,
        IReadOnlySet<string>? profileExcluded = null)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(served);

        var mismatches = new List<string>();
        foreach (var name in declared.Keys.Union(served.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var isDeclared = declared.TryGetValue(name, out var declaredVersion);
            var isServed = served.TryGetValue(name, out var servedVersion);
            if (profileExcluded is not null && profileExcluded.Contains(name))
            {
                if (isServed)
                {
                    mismatches.Add($"'{name}' is served as '{servedVersion}' but the compiled protocol profile excludes it");
                }

                continue;
            }

            if (!isServed)
            {
                mismatches.Add($"'{name}' is declared as '{declaredVersion}' but this server does not serve it");
            }
            else if (!isDeclared)
            {
                mismatches.Add($"'{name}' is served as '{servedVersion}' but is not declared");
            }
            else if (!string.Equals(declaredVersion, servedVersion, StringComparison.Ordinal))
            {
                mismatches.Add($"'{name}' is declared as '{declaredVersion}' but served as '{servedVersion}'");
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                "eng/release/component-versions.v1.json contractVersions do not match the contract versions this server serves: " +
                string.Join("; ", mismatches) +
                ". Update the declaration in the same change that bumps a contract version (AGENTS.md, Release component versions).");
        }
    }
}
