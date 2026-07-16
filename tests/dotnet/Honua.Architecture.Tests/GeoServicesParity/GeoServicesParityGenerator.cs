// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Architecture.Tests.GeoServicesParity;

/// <summary>
/// Generates the published GeoServices REST parity matrix
/// (<c>docs/gis/data/geoservices-rest-parity.json</c>) by joining the derived route
/// roster (<see cref="GeoServicesRouteRoster"/>) with the hand-authored judgement
/// source (<c>docs/gis/data/geoservices-parity-judgment.json</c>) — #2861 / #2863.
/// </summary>
/// <remarks>
/// <para>
/// The matrix has exactly two honestly-different halves, and this type is where they
/// meet:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Which routes exist</b> is mechanical. It is derived — the published
///     <c>esriPaths</c>, <c>honuaEndpoints</c>, and <c>capabilityMaturity</c> of every served
///     operation come from the generated capability data and are never authored.
///     Nothing can be published here that is not served.
///   </description></item>
///   <item><description>
///     <b>How completely each is implemented</b> is human judgement. The
///     implemented/partial/stub vocabulary and the gap prose come from the judgement
///     source and are never inferred. Nothing mechanical may promote a status.
///   </description></item>
/// </list>
/// <para>
/// The join itself is the gate (<see cref="Join"/> reports both directions through
/// <see cref="GeoServicesParityJoin"/>; <c>GeoServicesParityMatrixDriftTests</c>
/// asserts them). Generation is total: a served operation with no judgement, or a
/// judgement naming an unserved operation, is a failure, not a silent omission —
/// which is how a fabricated <c>computeClass</c> route stayed published, and how the
/// shipped async exportTiles lifecycle stayed published as deferred.
/// </para>
/// </remarks>
internal static class GeoServicesParityGenerator
{
    /// <summary>Published matrix schema version. Bumped by #2863 (roster is now derived).</summary>
    public const int SchemaVersion = 2;

    /// <summary>Repo-relative path of the hand-authored judgement source.</summary>
    public const string JudgmentRelativePath = "docs/gis/data/geoservices-parity-judgment.json";

    /// <summary>Repo-relative path of the generated, published matrix.</summary>
    public const string MatrixRelativePath = "docs/gis/data/geoservices-rest-parity.json";

    /// <summary>Statuses a served operation may carry.</summary>
    public static readonly string[] ServedStatuses = ["implemented", "partial", "stub"];

    /// <summary>
    /// Joins the derived roster against the judgement source and reports every
    /// mismatch in both directions without throwing, so the gate can present all of
    /// them at once rather than one per run.
    /// </summary>
    public static GeoServicesParityJoin Join()
    {
        var roster = GeoServicesRouteRoster.Derive();
        var judgment = LoadJudgment();

        var rosterByPath = roster.Operations.ToDictionary(
            operation => operation.EsriPath, StringComparer.Ordinal);

        var claims = judgment.ServiceList
            .SelectMany(service => service.OperationList.Select(operation => (Service: service, Operation: operation)))
            .SelectMany(pair => pair.Operation.PathList.Select(path => (pair.Service, pair.Operation, Path: path)))
            .ToArray();

        var claimedPaths = claims
            .GroupBy(claim => claim.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var unclassified = rosterByPath.Keys
            .Where(path => !claimedPaths.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{path} (served as {string.Join(", ", rosterByPath[path].HonuaEndpoints)})")
            .ToArray();

        var notServed = claimedPaths
            .Where(pair => !rosterByPath.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} (claimed {pair.Value[0].Operation.Status} by {pair.Value[0].Service.Id}/{pair.Value[0].Operation.Name})")
            .ToArray();

        var claimedTwice = claimedPaths
            .Where(pair => pair.Value.Length > 1)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} claimed by {string.Join(" and ", pair.Value.Select(claim => $"{claim.Service.Id}/{claim.Operation.Name} ({claim.Operation.Status})"))}")
            .ToArray();

        var servedAbsent = judgment.ServiceList
            .SelectMany(service => service.AbsentList.Select(operation => (Service: service, Operation: operation)))
            .Where(pair => !pair.Operation.EsriPath.Contains('*', StringComparison.Ordinal))
            .Where(pair => rosterByPath.ContainsKey(pair.Operation.EsriPath))
            .OrderBy(pair => pair.Operation.EsriPath, StringComparer.Ordinal)
            .Select(pair => $"{pair.Service.Id}/{pair.Operation.Name}: {pair.Operation.EsriPath}")
            .ToArray();

        var misfiled = claims
            .Where(claim => rosterByPath.TryGetValue(claim.Path, out var operation)
                && GeoServicesRouteRoster.ServiceIdByServiceType.TryGetValue(operation.ServiceType, out var serviceId)
                && !string.Equals(serviceId, claim.Service.Id, StringComparison.Ordinal))
            .OrderBy(claim => claim.Path, StringComparer.Ordinal)
            .Select(claim => $"{claim.Path} is served by {rosterByPath[claim.Path].ServiceType} but classified under '{claim.Service.Id}'")
            .ToArray();

        var homeless = rosterByPath.Values
            .Where(operation => !GeoServicesRouteRoster.ServiceIdByServiceType.ContainsKey(operation.ServiceType))
            .Select(operation => operation.ServiceType)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var staleExclusions = GeoServicesRouteRoster.ExcludedServiceTypes.Keys
            .Where(excluded => !roster.MatchedExclusions.Contains(excluded, StringComparer.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return new GeoServicesParityJoin
        {
            Roster = roster,
            Judgment = judgment,
            ServedButUnclassified = unclassified,
            ClassifiedButNotServed = notServed,
            ClassifiedTwice = claimedTwice,
            NotImplementedButServed = servedAbsent,
            MisfiledUnderWrongService = misfiled,
            ServiceTypesWithNoMatrixHome = homeless,
            StaleExclusions = staleExclusions,
        };
    }

    /// <summary>
    /// Projects the join onto the published matrix document. Assumes the join closes;
    /// the drift guard runs the join assertions first so a failure is reported as the
    /// specific mismatch rather than as a generation crash.
    /// </summary>
    public static ParityMatrix Generate()
    {
        var join = Join();
        var rosterByPath = join.Roster.Operations.ToDictionary(
            operation => operation.EsriPath, StringComparer.Ordinal);

        var services = join.Judgment.ServiceList
            .Select(service =>
            {
                var operations = service.OperationList
                    .Where(operation => operation.HonuaExtension is null)
                    .Select(operation => Project(operation, rosterByPath))
                    .ToArray();

                var extensions = service.OperationList
                    .Where(operation => operation.HonuaExtension is not null)
                    .Select(operation => Project(operation, rosterByPath))
                    .ToArray();

                var buckets = new ParityOperationBuckets
                {
                    Implemented = Bucket(operations, "implemented"),
                    Partial = Bucket(operations, "partial"),
                    Stub = Bucket(operations, "stub"),
                    NotImplemented = service.AbsentList
                        .OrderBy(operation => operation.EsriPath, StringComparer.Ordinal)
                        .ToArray(),
                };

                return new ParityService
                {
                    Id = service.Id,
                    DisplayName = service.DisplayName,
                    Parity = service.Parity,
                    DrillDownDoc = service.DrillDownDoc,
                    ImplementedSurface = service.ImplementedSurface ?? [],
                    KnownGaps = service.KnownGaps ?? [],
                    Evidence = service.EvidenceMap,
                    Operations = buckets,
                    HonuaExtensions = extensions.Length == 0 ? null : extensions,
                    ParameterCoverage = service.ParameterCoverage,
                    KnownLimitations = service.KnownLimitations,
                };
            })
            .ToArray();

        return new ParityMatrix
        {
            SchemaVersion = SchemaVersion,
            Generator = "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesParityGenerator.cs",
            TrackingIssue = "#2863",
            LastReviewed = join.Judgment.LastReviewed,
            JudgmentSource = JudgmentRelativePath,
            RouteRoster = new ParityRosterProvenance
            {
                DerivedFrom = "Honua.Server.EndpointRegistry.All, via the generated feature catalog "
                    + "(tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogGenerator.cs).",
                Normalization = "A served route is normalized to its Esri-relative operation path by "
                    + "stripping route constraints ({layerId:int} -> {layerId}) and dropping the service-instance "
                    + "address: under /rest/services/, every segment before the first segment ending in 'Server' "
                    + "is the address of a service instance, not part of the operation's identity. "
                    + "/rest/services/Utilities/Geometry/GeometryServer/clip and "
                    + "/rest/services/{id}/ImageServer/exportImage therefore normalize to /GeometryServer/clip and "
                    + "/ImageServer/exportImage. /sharing/rest/* and the catalog roots /rest/info and /rest/services "
                    + "carry no service address and normalize to themselves. The HTTP method is not part of the key, "
                    + "so the GET and POST forms of one Esri operation are one operation carrying one judgement; "
                    + "honuaEndpoints lists every served METHOD /path behind it.",
                Note = "esriPaths, honuaEndpoints, and capabilityMaturity on every served operation below are DERIVED "
                    + "and must not be hand-edited. capabilityMaturity is the ADR-0058 capability tier (is this route "
                    + "in the release?), NOT the parity status (how much of Esri's behaviour does it support?) — a stub "
                    + "operation on an in-release route correctly reads status=stub, capabilityMaturity=[implemented]. "
                    + "Only status, name, notes, and the prose fields are authored, in "
                    + "docs/gis/data/geoservices-parity-judgment.json. Regenerate with "
                    + "scripts/generate-geoservices-parity.sh.",
                ExcludedServiceTypes = GeoServicesRouteRoster.ExcludedServiceTypes
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new ParityExclusion { ServiceType = pair.Key, Reason = pair.Value })
                    .ToArray(),
            },
            CanonicalDocs = new ParityCanonicalDocs
            {
                LandingPage = "docs/reference/compatibility/geoservices-parity.md",
                MachineReadableExport = MatrixRelativePath,
                JudgmentSource = JudgmentRelativePath,
            },
            StatusVocabulary = join.Judgment.Vocabulary,
            Services = services,
        };
    }

    /// <summary>Serializes the matrix with the same deterministic, LF-pinned formatting the feature catalog uses.</summary>
    public static string Serialize(ParityMatrix matrix)
        => JsonSerializer.Serialize(matrix, ParityMatrixJsonContext.Default.ParityMatrix)
            .ReplaceLineEndings("\n") + "\n";

    /// <summary>Reads and deserializes the hand-authored judgement source.</summary>
    public static ParityJudgment LoadJudgment()
    {
        var path = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            JudgmentRelativePath.Replace('/', Path.DirectorySeparatorChar));

        return JsonSerializer.Deserialize(File.ReadAllText(path), ParityMatrixJsonContext.Default.ParityJudgment)
            ?? throw new InvalidOperationException($"{JudgmentRelativePath} did not deserialize into a judgement document.");
    }

    /// <summary>Absolute path of the committed, generated matrix.</summary>
    public static string CommittedMatrixPath()
        => ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            MatrixRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static ParityOperation[] Bucket(IEnumerable<ParityOperation> operations, string status)
        => operations
            .Where(operation => string.Equals(operation.Status, status, StringComparison.Ordinal))
            .OrderBy(operation => operation.EsriPaths.Length == 0 ? string.Empty : operation.EsriPaths[0], StringComparer.Ordinal)
            .ThenBy(operation => operation.Name, StringComparer.Ordinal)
            .ToArray();

    private static ParityOperation Project(
        JudgmentOperation operation,
        Dictionary<string, GeoServicesOperation> rosterByPath)
    {
        var paths = operation.PathList.OrderBy(path => path, StringComparer.Ordinal).ToArray();

        return new ParityOperation
        {
            Name = operation.Name,
            Status = operation.Status,
            EsriPaths = paths,
            HonuaEndpoints = paths
                .SelectMany(path => rosterByPath[path].HonuaEndpoints)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            CapabilityMaturity = paths
                .Select(path => rosterByPath[path].Maturity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            HonuaExtension = operation.HonuaExtension,
            Notes = operation.Notes,
        };
    }
}
