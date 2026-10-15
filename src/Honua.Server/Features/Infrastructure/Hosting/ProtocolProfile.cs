// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Capabilities;

namespace Honua.Infrastructure.Hosting;

/// <summary>
/// Compiled protocol profile. The symbols come from eng/Honua.ProtocolProfiles.props.
/// Runtime configuration can narrow a request and cannot turn an excluded module back on.
/// </summary>
internal static class ProtocolProfile
{
    public const string Format = "honua.protocol-profile/v1";

    public static readonly string[] SupportedNames = ["gsr", "ogc-api", "ogc-api-classic", "all"];

#if HONUA_PROTOCOL_GSR
    public const string Name = "gsr";
#elif HONUA_PROTOCOL_OGC_API_CLASSIC
    public const string Name = "ogc-api-classic";
#elif HONUA_PROTOCOL_OGC_API
    public const string Name = "ogc-api";
#elif HONUA_PROTOCOL_ALL
    public const string Name = "all";
#else
#error Honua protocol profile symbols were not imported. Import eng/Honua.ProtocolProfiles.props.
#endif

#if HONUA_EXCLUDE_GEOSERVICES
    public const bool IncludesGeoServices = false;
#else
    public const bool IncludesGeoServices = true;
#endif

#if HONUA_EXCLUDE_OGC_API
    public const bool IncludesOgcApi = false;
#else
    public const bool IncludesOgcApi = true;
#endif

#if HONUA_EXCLUDE_OGC_CLASSIC
    public const bool IncludesOgcClassic = false;
#else
    public const bool IncludesOgcClassic = true;
#endif

#if HONUA_EXCLUDE_ODATA
    public const bool IncludesOData = false;
#else
    public const bool IncludesOData = true;
#endif

#if HONUA_EXCLUDE_STAC
    public const bool IncludesStac = false;
#else
    public const bool IncludesStac = true;
#endif

#if HONUA_EXCLUDE_SENSORTHINGS
    public const bool IncludesSensorThings = false;
#else
    public const bool IncludesSensorThings = true;
#endif

#if HONUA_EXCLUDE_SCENE
    public const bool IncludesScene = false;
#else
    public const bool IncludesScene = true;
#endif

#if HONUA_EXCLUDE_GRPC
    public const bool IncludesGrpc = false;
#else
    public const bool IncludesGrpc = true;
#endif

#if HONUA_EXCLUDE_MCP
    public const bool IncludesMcp = false;
#else
    public const bool IncludesMcp = true;
#endif

    /// <summary>Contract names in release/component-versions.json that this profile does not serve.</summary>
    public static IReadOnlySet<string> ExcludedContractNames { get; } = CreateExcludedContracts();

    public static bool IncludesModule(string module) => module switch
    {
        "geoservices" => IncludesGeoServices,
        "ogc-api" => IncludesOgcApi,
        "ogc-classic" => IncludesOgcClassic,
        "odata" => IncludesOData,
        "stac" => IncludesStac,
        "sensorthings" => IncludesSensorThings,
        "scene" => IncludesScene,
        "grpc" => IncludesGrpc,
        "mcp" => IncludesMcp,
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="capabilityId"/> is protocol-neutral or its module is compiled in.
    /// Prefixes match eng/protocol-profiles.v1.json.
    /// </summary>
    public static bool IsCapabilityCompiled(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        var module = ModuleForCapability(capabilityId);
        return module is null || IncludesModule(module);
    }

    public static void ValidateRuntime(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var requested = configuration["HONUA_PROTOCOL_PROFILE"];
        if (!string.IsNullOrWhiteSpace(requested)
            && !string.Equals(requested.Trim(), Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"HONUA_PROTOCOL_PROFILE is '{requested.Trim()}' but this build compiled profile '{Name}'. " +
                "Runtime policy cannot enable a module this build excluded. Rebuild with " +
                $"-p:HonuaProtocolProfile={requested.Trim()} or Dockerfile ARG HONUA_PROTOCOL_PROFILE. " +
                "Supported profiles: gsr, ogc-api, ogc-api-classic, all.");
        }

        foreach (var requestedModule in ReadEnabledModules(configuration))
        {
            if (!TryCanonicalModule(requestedModule, out var canonical))
            {
                throw new InvalidOperationException(
                    $"Protocols:Enabled lists '{requestedModule}', which is not a protocol module. " +
                    "Supported modules: geoservices, ogc-api, ogc-classic, odata, stac, sensorthings, scene, grpc, mcp.");
            }

            if (!IncludesModule(canonical))
            {
                throw new InvalidOperationException(
                    $"Protocols:Enabled requests '{canonical}', which protocol profile '{Name}' does not compile. " +
                    "Remove it from Protocols:Enabled or rebuild with -p:HonuaProtocolProfile=all " +
                    "(Dockerfile ARG HONUA_PROTOCOL_PROFILE). Supported profiles: gsr, ogc-api, ogc-api-classic, all.");
            }
        }

        RejectExcludedExperimental(configuration, "serve.sensorthings", IncludesSensorThings);
        RejectExcludedExperimental(configuration, "scene.catalog", IncludesScene);
        RejectExcludedExperimental(configuration, "serve.3d-tiles-scene", IncludesScene);
        RejectExcludedExperimental(configuration, "serve.i3s-scene", IncludesScene);
        RejectExcludedExperimental(configuration, "scene.bim-ingest", IncludesScene);
        RejectExcludedExperimental(configuration, "scene.pointcloud-ingest", IncludesScene);

        var receiptPath = Path.Combine(AppContext.BaseDirectory, "honua-protocol-profile.json");
        if (File.Exists(receiptPath))
        {
            ProtocolProfileReceipt.Check(Name, File.ReadAllText(receiptPath));
        }
    }

    internal static string? ModuleForCapability(string capabilityId)
    {
        foreach (var (prefix, module, exact) in CapabilityRules)
        {
            if (exact)
            {
                if (string.Equals(capabilityId, prefix, StringComparison.Ordinal))
                {
                    return module;
                }
            }
            else if (capabilityId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return module;
            }
        }

        return null;
    }

    private static readonly (string Prefix, string Module, bool Exact)[] CapabilityRules =
    [
        ("serve.geoservices-", "geoservices", false),
        ("edit.geoservices-", "geoservices", false),
        ("versioning.branch", "geoservices", true),
        ("analysis.spatial", "geoservices", false),
        ("serve.ogc-api-", "ogc-api", false),
        ("process.ogc-api-", "ogc-api", false),
        ("styling.ogc-api-", "ogc-api", false),
        ("serve.wfs", "ogc-classic", true),
        ("serve.wms", "ogc-classic", true),
        ("serve.wmts", "ogc-classic", true),
        ("serve.wcs", "ogc-classic", true),
        ("serve.wps", "ogc-classic", true),
        ("serve.odata", "odata", true),
        ("serve.stac", "stac", true),
        ("serve.sensorthings", "sensorthings", true),
        ("serve.3d-tiles-scene", "scene", true),
        ("serve.i3s-scene", "scene", true),
        ("scene.catalog", "scene", true),
        ("scene.bim-ingest", "scene", true),
        ("scene.pointcloud-ingest", "scene", true),
        ("serve.grpc", "grpc", true),
        ("transport.grpc", "grpc", true),
        ("transport.grpc-web", "grpc", true),
        ("transport.native-grpc", "grpc", true),
        ("transport.mcp", "mcp", true),
    ];

    private static HashSet<string> CreateExcludedContracts()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
#if HONUA_EXCLUDE_GEOSERVICES
        names.Add("geoservices");
#endif
#if HONUA_EXCLUDE_OGC_API && HONUA_EXCLUDE_OGC_CLASSIC
        names.Add("ogc");
#endif
#if HONUA_EXCLUDE_STAC
        names.Add("stac");
#endif
#if HONUA_EXCLUDE_GRPC
        names.Add("grpc");
#endif
        return names;
    }

    private static IEnumerable<string> ReadEnabledModules(IConfiguration configuration)
    {
        var section = configuration.GetSection("Protocols:Enabled");
        if (!section.Exists())
        {
            yield break;
        }

        var values = section.Get<string[]>();
        // A comma-separated scalar is the env-var form (Protocols__Enabled=odata,stac).
        // The binder returns null or an empty array when that scalar has no indexed children.
        if (!string.IsNullOrWhiteSpace(section.Value) && (values is null || values.Length == 0))
        {
            values = section.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (values is null)
        {
            yield break;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim();
            }
        }
    }

    private static void RejectExcludedExperimental(IConfiguration configuration, string capabilityId, bool included)
    {
        if (included || !CapabilityFlagOptions.IsExperimentalEnabled(configuration, capabilityId))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Capability '{capabilityId}' is explicitly enabled but protocol profile '{Name}' does not compile that module. " +
            "Turn the experimental flag off or rebuild with -p:HonuaProtocolProfile=all " +
            "(Dockerfile ARG HONUA_PROTOCOL_PROFILE). Supported profiles: gsr, ogc-api, ogc-api-classic, all.");
    }

    internal static bool TryCanonicalModule(string name, out string canonical)
    {
        canonical = name.Trim().ToLowerInvariant() switch
        {
            "geoservices" or "gsr" => "geoservices",
            "ogcapi" or "ogc-api" or "ogc" => "ogc-api",
            "ogcclassic" or "ogc-api-classic" or "ogc-classic" => "ogc-classic",
            "odata" => "odata",
            "stac" => "stac",
            "sensorthings" or "sensor-things" => "sensorthings",
            "scene" or "i3s" or "sceneserver" => "scene",
            "grpc" => "grpc",
            "mcp" => "mcp",
            _ => string.Empty,
        };
        return canonical.Length > 0;
    }
}

/// <summary>Reads the one-line receipt written next to the build and publish output.</summary>
internal static class ProtocolProfileReceipt
{
    public static void Check(string expectedProfile, string receiptJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(receiptJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("honua-protocol-profile.json is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var formatNode)
                || formatNode.GetString() != ProtocolProfile.Format)
            {
                throw new InvalidOperationException(
                    $"honua-protocol-profile.json format is not {ProtocolProfile.Format}.");
            }

            var profile = root.TryGetProperty("profile", out var profileNode) ? profileNode.GetString() : null;
            if (!string.Equals(profile, expectedProfile, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"honua-protocol-profile.json profile is '{profile}' but '{expectedProfile}' was required. " +
                    "Do not reuse a publish output or intermediate cache from a different protocol profile.");
            }

            if (!root.TryGetProperty("modules", out var modulesNode) || modulesNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("honua-protocol-profile.json is missing modules.");
            }

            var modules = modulesNode.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
            var expected = ExpectedModules(expectedProfile);
            if (!modules.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"honua-protocol-profile.json modules [{string.Join(",", modules)}] do not match profile '{expectedProfile}' " +
                    $"[{string.Join(",", expected)}].");
            }
        }
    }

    public static string[] ExpectedModules(string profile) => profile switch
    {
        "gsr" => ["geoservices"],
        "ogc-api" => ["ogc-api"],
        "ogc-api-classic" => ["ogc-api", "ogc-classic"],
        "all" => ["geoservices", "ogc-api", "ogc-classic", "odata", "stac", "sensorthings", "scene", "grpc", "mcp"],
        _ => throw new InvalidOperationException(
            $"Unsupported protocol profile '{profile}'. Supported profiles: gsr, ogc-api, ogc-api-classic, all."),
    };
}
