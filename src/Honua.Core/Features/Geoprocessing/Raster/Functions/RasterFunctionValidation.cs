// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster.Functions;

/// <summary>Bounded structural limits for untrusted raster-function definitions.</summary>
public sealed record RasterFunctionValidationOptions
{
    /// <summary>Default validation limits.</summary>
    public static RasterFunctionValidationOptions Default { get; } = new();

    /// <summary>Maximum nodes in one definition.</summary>
    public int MaxNodes { get; init; } = 32;

    /// <summary>Maximum dependency depth from an input to the output.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Maximum inputs to one node.</summary>
    public int MaxFanIn { get; init; } = 8;

    /// <summary>Maximum one-based bands named by one node.</summary>
    public int MaxBands { get; init; } = 64;

    /// <summary>
    /// Absolute cell ceiling for an explicitly dimensioned output. Runtime admission may
    /// impose a lower ceiling after resolving inherited source dimensions.
    /// </summary>
    public long MaxOutputCells { get; init; } = 100_000_000;

    /// <summary>Maximum colormap entries.</summary>
    public int MaxColormapEntries { get; init; } = 256;

    /// <summary>Maximum reclassification ranges.</summary>
    public int MaxReclassificationRules { get; init; } = 256;

    /// <summary>Maximum encoded clip geometry size.</summary>
    public int MaxClipGeometryBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum node or input-slot identifier length.</summary>
    public int MaxIdentifierLength { get; init; } = 64;
}

/// <summary>Stable validation codes for raster-function admission.</summary>
public static class RasterFunctionValidationCodes
{
    /// <summary>The graph contract version is unsupported.</summary>
    public const string UnsupportedVersion = "unsupported_version";

    /// <summary>The graph is empty or exceeds the node ceiling.</summary>
    public const string NodeBudgetExceeded = "node_budget_exceeded";

    /// <summary>A node or input-slot identifier is invalid or duplicated.</summary>
    public const string InvalidIdentifier = "invalid_identifier";

    /// <summary>A node input references a missing node.</summary>
    public const string MissingReference = "missing_reference";

    /// <summary>The graph contains a dependency cycle.</summary>
    public const string CycleDetected = "cycle_detected";

    /// <summary>The output dependency chain is too deep.</summary>
    public const string DepthExceeded = "depth_exceeded";

    /// <summary>A node has an invalid number of inputs.</summary>
    public const string InvalidFanIn = "invalid_fan_in";

    /// <summary>A typed node parameter is malformed or outside its bounded range.</summary>
    public const string InvalidParameter = "invalid_parameter";

    /// <summary>A node is disconnected from the declared output.</summary>
    public const string DisconnectedNode = "disconnected_node";

    /// <summary>An invocation does not bind the definition's input slots exactly.</summary>
    public const string InvalidSourceBinding = "invalid_source_binding";
}

/// <summary>A caller-safe raster-function validation failure.</summary>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Path">Definition path associated with the failure.</param>
/// <param name="Message">Caller-safe explanation.</param>
public sealed record RasterFunctionValidationError(string Code, string Path, string Message);

/// <summary>Result of validating a raster-function definition or invocation.</summary>
public sealed record RasterFunctionValidationResult
{
    /// <summary>All validation failures.</summary>
    public required IReadOnlyList<RasterFunctionValidationError> Errors { get; init; }

    /// <summary>Whether validation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates canonical raster-function definitions before planning or persistence.</summary>
public static class RasterFunctionValidator
{
    /// <summary>Validates one function definition against bounded structural limits.</summary>
    public static RasterFunctionValidationResult Validate(
        RasterFunctionDefinition definition,
        RasterFunctionValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        options ??= RasterFunctionValidationOptions.Default;
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<RasterFunctionValidationError>();
        if (definition.ContractVersion is < RasterFunctionContract.MinimumSupportedVersion
            or > RasterFunctionContract.CurrentVersion)
        {
            Add(errors, RasterFunctionValidationCodes.UnsupportedVersion, "contractVersion",
                $"Raster function contract version {definition.ContractVersion} is not supported.");
        }

        if (definition.Nodes.Count is 0 || definition.Nodes.Count > options.MaxNodes)
        {
            Add(errors, RasterFunctionValidationCodes.NodeBudgetExceeded, "nodes",
                $"Raster function definitions require 1 to {options.MaxNodes} nodes.");
            return Result(errors);
        }

        var nodesById = new Dictionary<string, RasterFunctionNode>(StringComparer.Ordinal);
        var inputNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < definition.Nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = definition.Nodes[index];
            var path = $"nodes[{index}]";
            if (!IsSafeIdentifier(node.Id, options.MaxIdentifierLength))
            {
                Add(errors, RasterFunctionValidationCodes.InvalidIdentifier, $"{path}.id",
                    "Node identifiers must be bounded ASCII names beginning with a letter.");
                continue;
            }

            if (!nodesById.TryAdd(node.Id, node))
            {
                Add(errors, RasterFunctionValidationCodes.InvalidIdentifier, $"{path}.id",
                    $"Node identifier '{node.Id}' is duplicated.");
            }

            if (node is RasterFunctionInputNode input
                && (!IsSafeIdentifier(input.InputName, options.MaxIdentifierLength)
                    || !inputNames.Add(input.InputName)))
            {
                Add(errors, RasterFunctionValidationCodes.InvalidIdentifier, $"{path}.inputName",
                    "Input slot names must be unique bounded ASCII names beginning with a letter.");
            }

            ValidateNode(node, path, options, errors);
        }

        if (!nodesById.TryGetValue(definition.OutputNodeId, out var outputNode))
        {
            Add(errors, RasterFunctionValidationCodes.MissingReference, "outputNodeId",
                "The output node does not exist in this definition.");
            return Result(errors);
        }

        foreach (var node in nodesById.Values)
        {
            foreach (var inputId in node.Inputs)
            {
                if (!nodesById.ContainsKey(inputId))
                {
                    Add(errors, RasterFunctionValidationCodes.MissingReference, $"nodes[{node.Id}].inputs",
                        $"Input node '{inputId}' does not exist.");
                }
            }
        }

        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        _ = Visit(outputNode, nodesById, state, depths, reachable, options.MaxDepth, errors, cancellationToken);

        foreach (var nodeId in nodesById.Keys)
        {
            if (!reachable.Contains(nodeId))
            {
                Add(errors, RasterFunctionValidationCodes.DisconnectedNode, $"nodes[{nodeId}]",
                    "Every node must contribute to the declared output.");
            }
        }

        return Result(errors);
    }

    /// <summary>
    /// Validates a definition and requires its input slots to match typed source bindings exactly.
    /// Descriptor-specific security and content checks remain owned by
    /// <see cref="RasterSourceDescriptorValidator"/> at submission.
    /// </summary>
    public static RasterFunctionValidationResult ValidateInvocation(
        RasterFunctionInvocation invocation,
        RasterFunctionValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        options ??= RasterFunctionValidationOptions.Default;
        var graphResult = Validate(invocation.Definition, options, cancellationToken);
        var errors = new List<RasterFunctionValidationError>(graphResult.Errors);
        var required = invocation.Definition.Nodes
            .OfType<RasterFunctionInputNode>()
            .Select(static node => node.InputName)
            .ToHashSet(StringComparer.Ordinal);

        if (invocation.Sources.Count > options.MaxNodes)
        {
            Add(errors, RasterFunctionValidationCodes.InvalidSourceBinding, "sources",
                $"A raster function invocation accepts at most {options.MaxNodes} source bindings.");
            return Result(errors);
        }

        foreach (var inputName in required)
        {
            if (!invocation.Sources.ContainsKey(inputName))
            {
                Add(errors, RasterFunctionValidationCodes.InvalidSourceBinding, $"sources.{inputName}",
                    $"Raster input slot '{inputName}' is not bound.");
            }
        }

        foreach (var binding in invocation.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!required.Contains(binding.Key))
            {
                Add(errors, RasterFunctionValidationCodes.InvalidSourceBinding, $"sources.{binding.Key}",
                    $"Raster source binding '{binding.Key}' is not declared by the definition.");
                continue;
            }

            var descriptorResult = RasterSourceDescriptorValidator.Validate(
                binding.Value,
                cancellationToken: cancellationToken);
            foreach (var descriptorError in descriptorResult.Errors)
            {
                Add(errors, descriptorError.Code, $"sources.{binding.Key}.{descriptorError.Field}",
                    descriptorError.Message);
            }
        }

        return Result(errors);
    }

    private static int Visit(
        RasterFunctionNode node,
        IReadOnlyDictionary<string, RasterFunctionNode> nodesById,
        Dictionary<string, VisitState> state,
        Dictionary<string, int> depths,
        HashSet<string> reachable,
        int maxDepth,
        List<RasterFunctionValidationError> errors,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state.TryGetValue(node.Id, out var currentState))
        {
            if (currentState == VisitState.Visiting)
            {
                Add(errors, RasterFunctionValidationCodes.CycleDetected, $"nodes[{node.Id}]",
                    "Raster function graphs cannot contain dependency cycles.");
            }

            return currentState == VisitState.Visited && depths.TryGetValue(node.Id, out var knownDepth)
                ? knownDepth
                : 0;
        }

        state[node.Id] = VisitState.Visiting;
        reachable.Add(node.Id);
        var childDepth = 0;
        foreach (var inputId in node.Inputs)
        {
            if (nodesById.TryGetValue(inputId, out var input))
            {
                childDepth = Math.Max(
                    childDepth,
                    Visit(input, nodesById, state, depths, reachable, maxDepth, errors, cancellationToken));
            }
        }

        state[node.Id] = VisitState.Visited;
        var depth = checked(childDepth + 1);
        depths[node.Id] = depth;
        if (depth > maxDepth)
        {
            Add(errors, RasterFunctionValidationCodes.DepthExceeded, $"nodes[{node.Id}]",
                $"Raster function dependency depth exceeds {maxDepth}.");
        }

        return depth;
    }

    private static void ValidateNode(
        RasterFunctionNode node,
        string path,
        RasterFunctionValidationOptions options,
        List<RasterFunctionValidationError> errors)
    {
        if (node.Inputs.Count > options.MaxFanIn)
        {
            Add(errors, RasterFunctionValidationCodes.InvalidFanIn, $"{path}.inputs",
                $"A raster function node accepts at most {options.MaxFanIn} inputs.");
        }

        switch (node)
        {
            case RasterFunctionInputNode:
                RequireInputs(node, 0, 0, path, errors);
                break;
            case RasterFunctionCompositeNode composite:
                RequireInputs(node, 2, options.MaxFanIn, path, errors);
                if (!Enum.IsDefined(composite.Method))
                {
                    Invalid(errors, $"{path}.method", "Composite method is not supported.");
                }

                break;
            default:
                RequireInputs(node, 1, 1, path, errors);
                break;
        }

        switch (node)
        {
            case RasterFunctionBandSelectNode bandSelect:
                if (bandSelect.Bands.Count is 0 || bandSelect.Bands.Count > options.MaxBands
                    || bandSelect.Bands.Any(band => band is <= 0 || band > options.MaxBands)
                    || bandSelect.Bands.Distinct().Count() != bandSelect.Bands.Count)
                {
                    Invalid(errors, $"{path}.bands",
                        $"Band selections require 1 to {options.MaxBands} unique indexes in the supported range.");
                }

                break;
            case RasterFunctionSpectralIndexNode spectral:
                if (!Enum.IsDefined(spectral.Method)
                    || spectral.PrimaryBand is <= 0 || spectral.PrimaryBand > options.MaxBands
                    || spectral.SecondaryBand is <= 0 || spectral.SecondaryBand > options.MaxBands
                    || spectral.PrimaryBand == spectral.SecondaryBand)
                {
                    Invalid(errors, path,
                        $"Spectral indices require an allowlisted method and two distinct bands from 1 to {options.MaxBands}.");
                }

                break;
            case RasterFunctionClipNode clip:
                if (clip.Region.Geometry is not { Length: > 0 } geometry
                    || geometry.Length > options.MaxClipGeometryBytes
                    || clip.Region.Srid is <= 0)
                {
                    Invalid(errors, $"{path}.region",
                        $"Clip WKB must contain 1 to {options.MaxClipGeometryBytes} bytes and use a positive SRID when specified.");
                }

                break;
            case RasterFunctionResampleNode resample:
                ValidateResample(resample, path, options, errors);
                break;
            case RasterFunctionReprojectNode reproject:
                if (reproject.OutputSrid <= 0 || !Enum.IsDefined(reproject.Algorithm))
                {
                    Invalid(errors, path, "Reprojection requires a positive output SRID and allowlisted resampling algorithm.");
                }

                break;
            case RasterFunctionStretchNode stretch:
                ValidateStretch(stretch, path, options, errors);
                break;
            case RasterFunctionColormapNode colormap:
                ValidateColormap(colormap, path, options, errors);
                break;
            case RasterFunctionTerrainNode terrain:
                ValidateTerrain(terrain, path, options, errors);
                break;
            case RasterFunctionReclassifyNode reclassify:
                ValidateReclassify(reclassify, path, options, errors);
                break;
        }
    }

    private static void ValidateResample(
        RasterFunctionResampleNode node,
        string path,
        RasterFunctionValidationOptions options,
        List<RasterFunctionValidationError> errors)
    {
        var hasDimensions = node.Width.HasValue || node.Height.HasValue;
        var hasPixelSize = node.PixelSize.HasValue;
        var dimensionsValid = node.Width is > 0 && node.Height is > 0;
        var pixelSizeValid = node.PixelSize is { } pixel
            && double.IsFinite(pixel.Width) && pixel.Width > 0
            && double.IsFinite(pixel.Height) && pixel.Height > 0;
        var cellCountValid = !dimensionsValid
            || checked((long)node.Width!.Value * node.Height!.Value) <= options.MaxOutputCells;
        if (hasDimensions == hasPixelSize
            || (hasDimensions && !dimensionsValid)
            || (hasPixelSize && !pixelSizeValid)
            || !cellCountValid
            || !Enum.IsDefined(node.Algorithm))
        {
            Invalid(errors, path,
                $"Resampling requires positive dimensions up to {options.MaxOutputCells} cells or a finite positive pixel size, plus an allowlisted algorithm.");
        }
    }

    private static void ValidateStretch(
        RasterFunctionStretchNode node,
        string path,
        RasterFunctionValidationOptions options,
        List<RasterFunctionValidationError> errors)
    {
        var stretch = node.Stretch;
        var statisticsPaired = (stretch.StatisticsMin is null) == (stretch.StatisticsMax is null);
        var statisticsValid = stretch.StatisticsMin is null
            || (stretch.StatisticsMin.Length is > 0
                && stretch.StatisticsMin.Length <= options.MaxBands
                && stretch.StatisticsMin.Length == stretch.StatisticsMax!.Length
                && stretch.StatisticsMin.Zip(stretch.StatisticsMax)
                    .All(static pair => double.IsFinite(pair.First)
                        && double.IsFinite(pair.Second)
                        && pair.First < pair.Second));
        if (!Enum.IsDefined(stretch.StretchType)
            || !double.IsFinite(stretch.NumberOfStandardDeviations)
            || stretch.NumberOfStandardDeviations <= 0
            || !double.IsFinite(stretch.MinPercent)
            || stretch.MinPercent is < 0 or > 100
            || !double.IsFinite(stretch.MaxPercent)
            || stretch.MaxPercent is < 0 or > 100
            || stretch.MinPercent + stretch.MaxPercent >= 100
            || !statisticsPaired
            || !statisticsValid)
        {
            Invalid(errors, $"{path}.stretch", "Stretch parameters or explicit statistics are invalid.");
        }
    }

    private static void ValidateColormap(
        RasterFunctionColormapNode node,
        string path,
        RasterFunctionValidationOptions options,
        List<RasterFunctionValidationError> errors)
    {
        var entries = node.Colormap.Entries;
        var previous = double.NegativeInfinity;
        var valid = entries.Count is > 0 && entries.Count <= options.MaxColormapEntries;
        foreach (var entry in entries)
        {
            valid &= double.IsFinite(entry.Value) && entry.Value > previous;
            previous = entry.Value;
        }

        if (!valid)
        {
            Invalid(errors, $"{path}.colormap",
                $"Colormaps require 1 to {options.MaxColormapEntries} finite, strictly increasing stops.");
        }
    }

    private static void ValidateTerrain(
        RasterFunctionTerrainNode node,
        string path,
        RasterFunctionValidationOptions options,
        List<RasterFunctionValidationError> errors)
    {
        var terrain = node.Terrain;
        if (!Enum.IsDefined(terrain.Method)
            || terrain.Band is <= 0 || terrain.Band > options.MaxBands
            || !double.IsFinite(terrain.ZFactor)
            || terrain.ZFactor <= 0
            || !double.IsFinite(terrain.AzimuthDegrees)
            || terrain.AzimuthDegrees is < 0 or > 360
            || !double.IsFinite(terrain.AltitudeDegrees)
            || terrain.AltitudeDegrees is < 0 or > 90)
        {
            Invalid(errors, $"{path}.terrain", "Terrain parameters are outside their bounded ranges.");
        }
    }

    private static void ValidateReclassify(
        RasterFunctionReclassifyNode node,
        string path,
        RasterFunctionValidationOptions options,
        List<RasterFunctionValidationError> errors)
    {
        var previousMaximum = double.NegativeInfinity;
        var valid = node.Rules.Count is > 0 && node.Rules.Count <= options.MaxReclassificationRules;
        foreach (var rule in node.Rules)
        {
            valid &= double.IsFinite(rule.Minimum)
                && double.IsFinite(rule.Maximum)
                && double.IsFinite(rule.Value)
                && rule.Minimum < rule.Maximum
                && rule.Minimum >= previousMaximum;
            previousMaximum = rule.Maximum;
        }

        valid &= Enum.IsDefined(node.OutputPixelType)
            && (!node.NoDataReplacement.HasValue || double.IsFinite(node.NoDataReplacement.Value));
        if (!valid)
        {
            Invalid(errors, path,
                $"Reclassification requires 1 to {options.MaxReclassificationRules} ordered, non-overlapping finite ranges.");
        }
    }

    private static void RequireInputs(
        RasterFunctionNode node,
        int minimum,
        int maximum,
        string path,
        List<RasterFunctionValidationError> errors)
    {
        if (node.Inputs.Count < minimum || node.Inputs.Count > maximum)
        {
            Add(errors, RasterFunctionValidationCodes.InvalidFanIn, $"{path}.inputs",
                $"This node requires between {minimum} and {maximum} inputs.");
        }

        if (node.Inputs.Any(static input => string.IsNullOrWhiteSpace(input)))
        {
            Add(errors, RasterFunctionValidationCodes.InvalidIdentifier, $"{path}.inputs",
                "Input node identifiers cannot be empty.");
        }

        if (node.Inputs.Distinct(StringComparer.Ordinal).Count() != node.Inputs.Count)
        {
            Add(errors, RasterFunctionValidationCodes.InvalidFanIn, $"{path}.inputs",
                "A node cannot reference the same upstream node more than once.");
        }
    }

    private static bool IsSafeIdentifier(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static void ValidateOptions(RasterFunctionValidationOptions options)
    {
        if (options.MaxNodes <= 0
            || options.MaxDepth <= 0
            || options.MaxFanIn < 2
            || options.MaxBands <= 0
            || options.MaxOutputCells <= 0
            || options.MaxColormapEntries <= 0
            || options.MaxReclassificationRules <= 0
            || options.MaxClipGeometryBytes <= 0
            || options.MaxIdentifierLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Raster function validation limits must be positive.");
        }
    }

    private static void Invalid(
        List<RasterFunctionValidationError> errors,
        string path,
        string message)
        => Add(errors, RasterFunctionValidationCodes.InvalidParameter, path, message);

    private static void Add(
        List<RasterFunctionValidationError> errors,
        string code,
        string path,
        string message)
        => errors.Add(new RasterFunctionValidationError(code, path, message));

    private static RasterFunctionValidationResult Result(List<RasterFunctionValidationError> errors)
        => new() { Errors = errors };

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
