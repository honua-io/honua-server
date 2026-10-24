// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.CustomCode;

namespace Honua.Geoprocessing.Cli;

/// <summary>How a process executes, which decides what it needs before it can run here.</summary>
public enum GpExecutionMode
{
    /// <summary>In-process managed code (NTS and friends); needs only the devkit itself.</summary>
    Managed,

    /// <summary>Native GDAL work; needs the worker image or the host GDAL CLIs.</summary>
    Native,

    /// <summary>Reads or writes a configured server resource; needs the integration stack.</summary>
    ServiceBacked,

    /// <summary>User code under the custom-code harness; needs the matching local harness.</summary>
    Custom,
}

/// <summary>One prerequisite and whether this machine satisfies it.</summary>
/// <param name="Name">What is needed.</param>
/// <param name="Status"><c>satisfied</c>, <c>missing</c>, or <c>unchecked</c>.</param>
/// <param name="Detail">Where it was looked for, or how to satisfy it.</param>
public sealed record GpPrerequisite(string Name, string Status, string Detail)
{
    /// <summary>Whether the prerequisite is known to be met.</summary>
    public bool Satisfied => Status == "satisfied";
}

/// <summary>
/// The devkit's answer to "can this registered function run on this machine?". Registration
/// alone never proves readiness: a native op needs GDAL, a service-backed op needs the
/// integration stack, custom code needs its harness. The verdict names the first missing
/// prerequisite so the developer knows what to install, and the devkit never answers a
/// blocked function by submitting it to a server.
/// </summary>
public sealed record GpRunnability
{
    /// <summary>The execution mode the process was classified into.</summary>
    public required GpExecutionMode ExecutionMode { get; init; }

    /// <summary>Every prerequisite checked, in order.</summary>
    public required IReadOnlyList<GpPrerequisite> Prerequisites { get; init; }

    /// <summary>Deployment targets the mode supports.</summary>
    public required IReadOnlyList<string> SupportedTargets { get; init; }

    /// <summary>True when every prerequisite is satisfied.</summary>
    public bool RunnableHere => Prerequisites.All(static p => p.Satisfied);

    /// <summary>The first missing or unchecked prerequisite, or null when runnable.</summary>
    public string? BlockedBy => Prerequisites.FirstOrDefault(static p => !p.Satisfied)?.Name;

    /// <summary>The mode as the JSON/text label.</summary>
    public string ExecutionModeLabel => ExecutionMode switch
    {
        GpExecutionMode.Managed => "managed",
        GpExecutionMode.Native => "native",
        GpExecutionMode.ServiceBacked => "service-backed",
        _ => "custom",
    };

    /// <summary>Renders the verdict as JSON fields merged into a describe/list descriptor.</summary>
    public void WriteTo(JsonObject descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor["executionMode"] = ExecutionModeLabel;
        var prerequisites = new JsonArray();
        foreach (var p in Prerequisites)
        {
            prerequisites.Add(new JsonObject { ["name"] = p.Name, ["status"] = p.Status, ["detail"] = p.Detail });
        }

        descriptor["prerequisites"] = prerequisites;
        descriptor["runnableHere"] = RunnableHere;
        descriptor["blockedBy"] = BlockedBy;
        var targets = new JsonArray();
        foreach (var target in SupportedTargets)
        {
            targets.Add(target);
        }

        descriptor["supportedTargets"] = targets;
    }
}

/// <summary>
/// Facts about this machine the runnability assessment needs. Each probe is cheap and
/// cached; the container probe is opt-in because it shells out to the container runtime.
/// Tests inject fixed answers.
/// </summary>
public sealed class GpEnvironmentProbe
{
    private readonly Func<string, bool> _executableAvailable;
    private readonly Func<string, bool> _configurationAvailable;
    private readonly Lazy<bool> _hostGdal;
    private readonly Lazy<bool> _python;
    private readonly Lazy<bool> _integrationStack;

    /// <summary>Creates a probe that inspects the real machine.</summary>
    /// <param name="workerImageAvailable">The container probe result when the caller ran it; null means unchecked.</param>
    /// <param name="workerImage">The worker image reference the container probe looked for.</param>
    public GpEnvironmentProbe(bool? workerImageAvailable = null, string workerImage = "honua-worker-etl")
    {
        WorkerImageAvailable = workerImageAvailable;
        WorkerImage = workerImage;
        _executableAvailable = OnPath;
        _configurationAvailable = static dependency => dependency == "Geoprocessing:ImageryInference"
            ? HasConfiguration("Geoprocessing:ImageryInference:Provider") && HasConfiguration("Geoprocessing:ImageryInference:Endpoint")
            : HasConfiguration(dependency);
        _hostGdal = new Lazy<bool>(static () => OnPath("ogr2ogr") && OnPath("gdal_translate"));
        _python = new Lazy<bool>(static () => OnPath("python3") || OnPath("python"));
        _integrationStack = new Lazy<bool>(static () =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HONUA_BASE_URL")));
    }

    /// <summary>Creates a probe with fixed answers (tests).</summary>
    public GpEnvironmentProbe(bool hostGdalAvailable, bool? workerImageAvailable, bool pythonAvailable, bool integrationStackConfigured, string workerImage = "honua-worker-etl",
        Func<string, bool>? executableAvailable = null, Func<string, bool>? configurationAvailable = null)
    {
        WorkerImageAvailable = workerImageAvailable;
        WorkerImage = workerImage;
        _executableAvailable = executableAvailable ?? (_ => hostGdalAvailable);
        _configurationAvailable = configurationAvailable ?? (_ => integrationStackConfigured);
        _hostGdal = new Lazy<bool>(() => hostGdalAvailable);
        _python = new Lazy<bool>(() => pythonAvailable);
        _integrationStack = new Lazy<bool>(() => integrationStackConfigured);
    }

    /// <summary>Whether <c>ogr2ogr</c> and <c>gdal_translate</c> resolve on PATH.</summary>
    public bool HostGdalAvailable => _hostGdal.Value;

    /// <summary>The container probe result, or null when it was not run.</summary>
    public bool? WorkerImageAvailable { get; }

    /// <summary>The worker image reference.</summary>
    public string WorkerImage { get; }

    /// <summary>Whether a Python interpreter resolves on PATH.</summary>
    public bool PythonAvailable => _python.Value;

    /// <summary>Whether a server connection or database connection string is configured in the environment.</summary>
    public bool IntegrationStackConfigured => _integrationStack.Value;

    /// <summary>Whether the selected process's executable resolves on PATH.</summary>
    public bool ExecutableAvailable(string executable) => _executableAvailable(executable);

    /// <summary>Whether the named configuration prerequisite is present.</summary>
    public bool ConfigurationAvailable(string dependency) => _configurationAvailable(dependency);

    private static bool HasConfiguration(string key)
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal)));

    private static bool OnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var names = OperatingSystem.IsWindows() ? new[] { executable + ".exe", executable + ".cmd", executable + ".bat" } : [executable];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    if (File.Exists(Path.Join(directory, name)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH segment: skip it.
                }
            }
        }

        return false;
    }
}

/// <summary>Classifies a process and checks its prerequisites against a probe.</summary>
public static class GpRunnabilityAssessor
{
    /// <summary>Assesses one registered process.</summary>
    /// <param name="definition">The catalog definition (null when the executor is registered without one).</param>
    /// <param name="processId">The process id.</param>
    /// <param name="runtimeProfile">The runtime profile the executor accepts (<c>native</c>, <c>custom-code</c>, or managed/null).</param>
    /// <param name="probe">The machine facts.</param>
    public static GpRunnability Assess(ProcessDefinition? definition, string processId, string? runtimeProfile, GpEnvironmentProbe probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(probe);

        var profile = RuntimeProfiles.Normalize(runtimeProfile ?? definition?.RuntimeProfile);
        var mode = string.Equals(profile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal) ? GpExecutionMode.Custom
            : string.Equals(profile, RuntimeProfiles.Native, StringComparison.Ordinal) ? GpExecutionMode.Native
            : !string.IsNullOrWhiteSpace(definition?.ConfigurationDependency) ? GpExecutionMode.ServiceBacked
            : GpExecutionMode.Managed;

        var prerequisites = new List<GpPrerequisite>();
        switch (mode)
        {
            case GpExecutionMode.Managed:
                prerequisites.Add(new GpPrerequisite("devkit runtime", "satisfied", "runs in-process with the devkit's .NET runtime"));
                break;
            case GpExecutionMode.Native:
                var tools = NativeTools(processId);
                var toolNames = string.Join(", ", tools);
                if (probe.WorkerImageAvailable == true)
                {
                    prerequisites.Add(new GpPrerequisite($"GDAL worker image ({probe.WorkerImage})", "satisfied", "container image present; run --real-worker uses it"));
                }
                else if (tools.Length > 0 && tools.All(probe.ExecutableAvailable))
                {
                    prerequisites.Add(new GpPrerequisite($"host native CLIs ({toolNames})", "satisfied",
                        probe.WorkerImageAvailable is null
                            ? "found on PATH; the worker image was not probed (describe --probe-container, or doctor)"
                            : "found on PATH; the worker image is not available"));
                }
                else
                {
                    prerequisites.Add(new GpPrerequisite("GDAL (worker image or host CLIs)", "missing",
                        probe.WorkerImageAvailable is null
                            ? $"Required native tools ({toolNames}) are not all on PATH and the worker image was not probed; build {probe.WorkerImage} (docker/worker-gdal/Dockerfile) or install GDAL"
                            : $"Required native tools ({toolNames}) are not all on PATH and {probe.WorkerImage} is not available; build it with docker/worker-gdal/Dockerfile or install GDAL"));
                }

                break;
            case GpExecutionMode.ServiceBacked:
                prerequisites.Add(new GpPrerequisite($"integration stack ({definition!.ConfigurationDependency})", probe.ConfigurationAvailable(definition.ConfigurationDependency!) ? "satisfied" : "missing",
                    probe.ConfigurationAvailable(definition.ConfigurationDependency!)
                        ? $"{definition.ConfigurationDependency} is configured"
                        : $"configure {definition.ConfigurationDependency} and start docker-compose.gp-dev.yml; the devkit never submits to a server for you"));
                break;
            default:
                prerequisites.Add(new GpPrerequisite("custom-code local harness", probe.PythonAvailable ? "satisfied" : "unchecked",
                    probe.PythonAvailable
                        ? "python3 is on PATH for Python tools; .NET tools use the devkit runtime"
                        : "python3 is not on PATH; Python tools cannot run locally until it is"));
                break;
        }

        return new GpRunnability
        {
            ExecutionMode = mode,
            Prerequisites = prerequisites,
            SupportedTargets = mode switch
            {
                GpExecutionMode.Managed => ["local", "compose", "kubernetes-jobs", "aws-batch"],
                GpExecutionMode.Native => ["local-container", "compose", "kubernetes-jobs", "aws-batch"],
                GpExecutionMode.ServiceBacked => ["compose", "kubernetes-jobs", "aws-batch"],
                _ => ["local-harness", "aws-batch"],
            },
        };
    }

    private static string[] NativeTools(string processId) => processId switch
    {
        "gdal.ogr2ogr" or "source.ogr" or "transform.reproject" => ["ogr2ogr"],
        "gdal.gdalwarp" or "raster.reproject" or "conversion.raster-reproject" or "raster.resample" or "raster.mosaic" => ["gdalwarp"],
        "surface.slope" or "surface.aspect" or "surface.hillshade" or "surface.rugosity-tri" or "surface.rugosity-tpi" or "surface.roughness" => ["gdaldem"],
        "surface.contour" => ["gdal_contour"],
        "surface.viewshed" => ["gdal_viewshed"],
        "raster.statistics" or "raster.histogram" => ["gdalinfo", "python3"],
        "raster.clip" or "raster.zonal-statistics" => ["gdalwarp", "python3"],
        "raster.interpolate-idw" => ["gdal_grid"],
        "raster.interpolate-kriging" => ["gdal_translate", "gdal_merge.py"],
        "raster.map-algebra" => ["gdal_calc.py", "python3"],
        "raster.spectral-index" => ["gdal_calc.py", "gdal_translate"],
        "raster.reclassify" => ["gdal_calc.py"],
        "conversion.polygonize" => ["gdal_polygonize.py", "ogr2ogr"],
        "conversion.rasterize" => ["gdal_rasterize"],
        "conversion.raster-format" => ["gdal_translate"],
        "proximity.euclidean-distance" or "proximity.euclidean-allocation" => ["python3"],
        "pcloud.translate" => ["pdal"],
        "coverage.multidim.metadata" => ["gdalmdiminfo", "gdalmdimtranslate", "gdalinfo"],
        _ => [],
    };

    /// <summary>
    /// A valid example value per parameter: the declared default, else the first allowed
    /// value, else a type-appropriate sample (a WKB point, a tiny GeoJSON document, ...).
    /// </summary>
    /// <param name="definition">The catalog definition.</param>
    public static JsonObject ExampleValues(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var examples = new JsonObject();
        foreach (var parameter in definition.Parameters)
        {
            examples[parameter.Name] = parameter.DefaultValue
                ?? (parameter.AllowedValues is { Count: > 0 } allowed ? allowed[0] : null)
                ?? parameter.ValueType switch
                {
                    ProcessParameterValueType.WholeNumber => "1",
                    ProcessParameterValueType.FloatingPoint => "1.5",
                    ProcessParameterValueType.Srid => "4326",
                    ProcessParameterValueType.Flag => "true",
                    ProcessParameterValueType.Wkb => "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
                    ProcessParameterValueType.WkbArray => "[\"AQEAAAAAAAAAAAAAAAAAAAAAAAAA\"]",
                    _ => parameter.AcceptsGeoJsonDataUri
                        ? "data:application/geo+json;base64,eyJ0eXBlIjoiRmVhdHVyZUNvbGxlY3Rpb24iLCJmZWF0dXJlcyI6W119"
                        : parameter.Name,
                };
        }

        return examples;
    }
}
