// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Postgres-backed network-dataset registry (Phase 0 of #1882). Resolves a dataset
/// id to its edge/vertex table names from the <c>honua.network_datasets</c> registry
/// table (provisioned by migration 062) when present, falling back to the built-in
/// <see cref="NetworkDataset.Default"/> so the default deployment behaves exactly as
/// before (zero behaviour change). There is no editing surface in Phase 0 — the
/// registry is read-only here; later phases add edge/vertex CRUD through the admin
/// API.
/// </summary>
/// <remarks>
/// The resolved table names are interpolated into the pgRouting edges-SQL string
/// (which cannot reference bound parameters), so every name returned to the provider
/// is validated against a strict schema-qualified-identifier pattern by
/// <see cref="ValidateIdentifier"/>; a registry row with a non-conforming table name
/// is rejected rather than embedded. The built-in default's names are trusted
/// constants.
/// </remarks>
internal sealed partial class NetworkDatasetRegistry : INetworkDatasetResolver
{
    private static readonly string[] PgRoutingNumericDataTypes =
    [
        "smallint",
        "integer",
        "bigint",
        "numeric",
        "real",
        "double precision",
    ];

    private readonly IDatabaseSessionFactory _sessionFactory;
    private readonly ILogger<NetworkDatasetRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkDatasetRegistry"/> class.
    /// </summary>
    /// <param name="sessionFactory">Shared database session factory.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public NetworkDatasetRegistry(IDatabaseSessionFactory sessionFactory, ILogger<NetworkDatasetRegistry> logger)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<NetworkDataset?> ResolveAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        // Try the registry table first. When it is absent (plain-PostGIS images that
        // skip the routing migration, or pre-migration databases) or has no matching
        // row, fall back to the built-in default for the "default" id so routing
        // keeps working unchanged.
        var fromRegistry = await TryResolveFromRegistryAsync(datasetId, cancellationToken).ConfigureAwait(false);
        if (fromRegistry is not null)
        {
            return fromRegistry;
        }

        return string.Equals(datasetId, NetworkDataset.DefaultId, StringComparison.OrdinalIgnoreCase)
            ? NetworkDataset.Default
            : null;
    }

    private async Task<NetworkDataset?> TryResolveFromRegistryAsync(
        string datasetId,
        CancellationToken cancellationToken)
    {
        // Guarded by to_regclass so a missing registry table degrades to the default
        // path instead of throwing. Reads only the active dataset row.
        const string sql = """
            SELECT id, name, edge_table, vertex_table, srid, travel_profiles::text
            FROM honua.network_datasets
            WHERE id = @id AND status = 'active'
            LIMIT 1;
            """;

        try
        {
            await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Cheap existence probe avoids a hard failure when the registry table has
            // not been provisioned (e.g. plain-PostGIS image without pgRouting).
            var registryExists = await session.QuerySingleOrDefaultAsync(
                    "SELECT to_regclass('honua.network_datasets') IS NOT NULL;",
                    static row => !row.IsNull(0) && row.GetFieldValue<bool>(0),
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!registryExists)
            {
                return null;
            }

            var dataset = await session.QuerySingleOrDefaultAsync(
                    sql,
                    static row => new NetworkDataset(
                        row.GetFieldValue<string>(0),
                        row.GetFieldValue<string>(1),
                        row.GetFieldValue<string>(2),
                        row.GetFieldValue<string>(3),
                        row.GetFieldValue<int>(4))
                    {
                        TravelProfiles = ParseTravelProfiles(row.GetFieldValue<string>(5)),
                    },
                    new Dictionary<string, object?> { ["id"] = datasetId },
                    cancellationToken)
                .ConfigureAwait(false);

            if (dataset is null)
            {
                return null;
            }

            // Reject a registry row whose table names are not safe identifiers
            // rather than interpolating them into the edges-SQL string.
            ValidateIdentifier(dataset.EdgeTable, nameof(NetworkDataset.EdgeTable));
            ValidateIdentifier(dataset.VertexTable, nameof(NetworkDataset.VertexTable));
            var backedProfiles = await FilterBackedProfilesAsync(session, dataset, cancellationToken)
                .ConfigureAwait(false);
            return dataset with { TravelProfiles = backedProfiles };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            if (!string.Equals(datasetId, NetworkDataset.DefaultId, StringComparison.OrdinalIgnoreCase))
            {
                // A transient DB failure for a non-default dataset must not silently appear as
                // 'dataset not found'. Rethrow so the caller can surface a retriable error
                // instead of a permanent 404 (BH2-021).
                Log.TransientRegistryFailure(_logger, datasetId, ex);
                throw;
            }

            // Default dataset: swallow exceptions so routing falls through to the built-in
            // default topology unchanged (plain-PostGIS images without the registry table).
            return null;
        }
    }

    internal static List<RoutingTravelProfile> ParseTravelProfiles(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Travel profile metadata must be a JSON array.");
            }

            var profiles = new List<RoutingTravelProfile>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var profile = new RoutingTravelProfile(
                    element.GetProperty("name").GetString() ?? string.Empty,
                    element.GetProperty("forwardCostColumn").GetString() ?? string.Empty,
                    element.GetProperty("reverseCostColumn").GetString() ?? string.Empty);
                if (!NetworkDatasetValidation.IsValidTravelProfileName(profile.Name) ||
                    !NetworkDatasetValidation.IsValidColumnIdentifier(profile.ForwardCostColumn) ||
                    !NetworkDatasetValidation.IsValidColumnIdentifier(profile.ReverseCostColumn))
                {
                    throw new InvalidOperationException(
                        $"Network dataset travel profile '{profile.Name}' contains an unsafe identifier.");
                }

                if (profiles.Any(existing => string.Equals(
                        existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Network dataset travel profile '{profile.Name}' is duplicated.");
                }

                profiles.Add(profile);
            }

            EnsureDrivingProfile(profiles, "declared");
            return profiles;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Normalize every malformed JSON/shape/property/configuration failure to
            // InvalidOperationException. The registry's default-dataset fallback
            // deliberately excludes this type, so corruption can never silently
            // select the built-in topology. Do not include the raw JSON in the error.
            throw new InvalidOperationException(
                "Network dataset travel profile metadata is invalid.", ex);
        }
    }

    private static async Task<IReadOnlyList<RoutingTravelProfile>> FilterBackedProfilesAsync(
        IDatabaseSession session,
        NetworkDataset dataset,
        CancellationToken cancellationToken)
    {
        var tableParts = dataset.EdgeTable.Split('.', 2);
        var schema = tableParts.Length == 2 ? tableParts[0] : "public";
        var table = tableParts.Length == 2 ? tableParts[1] : tableParts[0];
        var requestedColumns = dataset.TravelProfiles
            .SelectMany(profile => new[] { profile.ForwardCostColumn, profile.ReverseCostColumn })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var numericColumns = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var column in session.QueryAsync(
                           "SELECT column_name FROM information_schema.columns " +
                           "WHERE table_schema = @schema AND table_name = @table " +
                           "AND column_name = ANY(@columns) AND data_type = ANY(@numeric_types) " +
                           "AND domain_name IS NULL;",
                           static row => row.GetFieldValue<string>(0),
                           new Dictionary<string, object?>
                           {
                               ["schema"] = schema,
                               ["table"] = table,
                               ["columns"] = requestedColumns,
                               ["numeric_types"] = PgRoutingNumericDataTypes,
                           },
                           cancellationToken).ConfigureAwait(false))
        {
            numericColumns.Add(column);
        }

        return FilterProfilesByColumns(dataset.TravelProfiles, numericColumns);
    }

    internal static IReadOnlyList<RoutingTravelProfile> FilterProfilesByColumns(
        IReadOnlyList<RoutingTravelProfile> profiles,
        IReadOnlySet<string> existingColumns)
    {
        var backedProfiles = profiles
            .Where(profile => existingColumns.Contains(profile.ForwardCostColumn) &&
                              existingColumns.Contains(profile.ReverseCostColumn))
            .ToArray();
        EnsureDrivingProfile(backedProfiles, "fully backed");
        return backedProfiles;
    }

    internal static bool IsPgRoutingNumericDataType(string dataType, string? domainName)
        => domainName is null && PgRoutingNumericDataTypes.Contains(dataType, StringComparer.Ordinal);

    private static void EnsureDrivingProfile(
        IReadOnlyList<RoutingTravelProfile> profiles,
        string state)
    {
        if (!profiles.Any(profile => string.Equals(
                profile.Name, PgRoutingProvider.DrivingTravelMode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Network dataset requires a {state} '{PgRoutingProvider.DrivingTravelMode}' travel profile.");
        }
    }

    /// <summary>
    /// Validates that a registered table name is a safe schema-qualified identifier
    /// before it is interpolated into the pgRouting edges-SQL string. Throws
    /// <see cref="InvalidOperationException"/> on a non-conforming name. Shares the rule
    /// with the admin editing surface via <see cref="NetworkDatasetValidation"/>.
    /// </summary>
    private static void ValidateIdentifier(string identifier, string role)
    {
        if (!NetworkDatasetValidation.IsValidTableIdentifier(identifier))
        {
            throw new InvalidOperationException(
                $"Network dataset {role} '{identifier}' is not a valid schema-qualified identifier.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 9300,
            Level = LogLevel.Warning,
            Message = "Network dataset registry lookup for dataset '{DatasetId}' failed with a DB error; " +
                      "routing request will receive a retriable error")]
        public static partial void TransientRegistryFailure(ILogger logger, string datasetId, Exception exception);
    }
}
