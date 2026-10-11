// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Migration;
using Honua.Protocols.Stac.Services;
using Honua.Server.Features.Admin;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Re-runs the reflection-disabled JSON call sites from honua-server#5633 in a child process.
/// The xUnit host keeps reflection enabled, so an in-process test cannot see the failure.
/// </summary>
internal static class ReflectionDisabledAotJsonProbe
{
    public const string Command = "aot-json-5633";
    public const string OperationId = "probe-upload-5633";

    public static async Task<int> ExecuteChildAsync(string[] args)
    {
        if (args is not [Command])
        {
            await Console.Error.WriteLineAsync($"usage: {Command}");
            return 2;
        }

        Console.WriteLine($"{ReflectionDisabledCloudNativeProbe.ReflectionStatePrefix}{JsonSerializer.IsReflectionEnabledByDefault}");
        try
        {
            _ = JsonSerializer.Serialize(new[] { "Point" });
            Console.WriteLine($"{ReflectionDisabledCloudNativeProbe.CanaryPrefix}serialized");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            Console.WriteLine($"{ReflectionDisabledCloudNativeProbe.CanaryPrefix}threw");
        }

        try
        {
            await WriteActiveOperationAsync();
            WriteStoredQuery();
            WriteMigrationEvidence();
            WriteStacFilter();
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
            return 1;
        }
    }

    public static async Task<ReflectionDisabledCloudNativeProbe.ProbeRun> RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("honua-5633-");
        try
        {
            var testAssembly = typeof(ReflectionDisabledAotJsonProbe).Assembly.Location;
            var runtimeConfig = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.ChangeExtension(testAssembly, ".runtimeconfig.json")))!;
            var runtimeOptions = runtimeConfig["runtimeOptions"]!.AsObject();
            if (runtimeOptions["configProperties"] is not JsonObject configProperties)
            {
                configProperties = [];
                runtimeOptions["configProperties"] = configProperties;
            }

            configProperties["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = false;
            var runtimeConfigPath = Path.Join(directory.FullName, "probe.runtimeconfig.json");
            await File.WriteAllTextAsync(runtimeConfigPath, runtimeConfig.ToJsonString());

            var host = Environment.ProcessPath is { } processPath
                && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal)
                    ? processPath
                    : "dotnet";
            var start = new ProcessStartInfo(host)
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(runtimeConfigPath);
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
            start.ArgumentList.Add(testAssembly);
            start.ArgumentList.Add(Command);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            return new ReflectionDisabledCloudNativeProbe.ProbeRun(process.ExitCode, await output, await errors, []);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task WriteActiveOperationAsync()
    {
        var progress = UploadProgress.CreateInitial(OperationId, "probe.geojson", 10) with
        {
            Status = OperationStatus.Processing
        };
        var store = new SeededProgressStore(progress);

        var active = await ExecuteAsync(await OperationsProgressEndpoints.HandleListActiveOperations(store));
        var activeOperation = RequireProperty(RequireProperty(active.Body, "operations")[0], "operationId").GetString();
        if (active.Status != StatusCodes.Status200OK || !string.Equals(activeOperation, OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GET /admin/operations/active returned {active.Status}: {active.Body.GetRawText()}");
        }

        var byType = await ExecuteAsync(await OperationsProgressEndpoints.HandleGetOperationsByType("Upload", store, CancellationToken.None));
        var typedOperation = RequireProperty(RequireProperty(byType.Body, "operations")[0], "operationId").GetString();
        if (byType.Status != StatusCodes.Status200OK || !string.Equals(typedOperation, OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GET /admin/operations/type/Upload returned {byType.Status}: {byType.Body.GetRawText()}");
        }

        var operation = RequireProperty(active.Body, "operations")[0];
        if (RequireProperty(operation, "type").GetInt32() != (int)OperationType.Upload
            || !string.Equals(RequireProperty(operation, "uploadId").GetString(), OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Active operation payload lost its progress fields: {operation.GetRawText()}");
        }

        Console.WriteLine($"active-status={active.Status}");
        Console.WriteLine($"type-status={byType.Status}");
        Console.WriteLine($"active-operation={activeOperation}");
    }

    private static void WriteStoredQuery()
    {
        var definition = new Wfs20Handler.StoredQueryDefinition(
            "urn:honua:probe",
            "Probe",
            null,
            "honua:Feature",
            "urn:ogc:def:queryLanguage:OGC-WFS::WFSQueryExpression",
            "honua:Feature",
            "<fes:Filter/>",
            [new Wfs20Handler.StoredQueryParameter("name", "xsd:string")]);
        var json = Wfs20Handler.SerializeStoredQueryDefinition(definition);
        var roundTrip = Wfs20Handler.DeserializeStoredQueryDefinition(json);
        if (roundTrip is null
            || !string.Equals(roundTrip.Id, definition.Id, StringComparison.Ordinal)
            || roundTrip.Parameters.Count != 1
            || !string.Equals(roundTrip.Parameters[0].Name, "name", StringComparison.Ordinal)
            || !string.Equals(roundTrip.FilterXml, definition.FilterXml, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Stored query JSON did not round-trip: {json}");
        }

        Console.WriteLine($"wfs-id={roundTrip.Id}");
    }

    private static void WriteMigrationEvidence()
    {
        var source = new MigrationSourceIdentity
        {
            DisplayName = "ArcGIS Source",
            BaseUrl = "https://example.com/arcgis/rest/services/Roads/FeatureServer"
        };
        var manifest = new MigrationManifestArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 1,
                TargetResourceCount = 1
            },
            TargetResources =
            [
                new MigrationManifestTargetResource
                {
                    SourceResourceId = "resource:Roads:layer:0",
                    SourceKind = "layer",
                    Action = "publish",
                    TargetResourceId = "target:resource:roads:layer-0",
                    TargetServiceName = "roads",
                    TargetResourceName = "layer-0",
                    Compatibility = new MigrationCompatibilityAssessment
                    {
                        Level = "compatible",
                        Reason = "Layer can be represented."
                    }
                }
            ]
        };
        var manifestJson = PostgresArcGisMigrationEvidenceStore.SerializeManifest(manifest);
        var manifestRoundTrip = PostgresArcGisMigrationEvidenceStore.DeserializeManifest(manifestJson);
        if (manifestRoundTrip is null
            || manifestRoundTrip.TargetResources.Length != 1
            || !string.Equals(manifestRoundTrip.SourceKind, manifest.SourceKind, StringComparison.Ordinal)
            || !string.Equals(manifestRoundTrip.TargetResources[0].TargetResourceId, "target:resource:roads:layer-0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Migration manifest JSON did not round-trip: {manifestJson}");
        }

        var parity = new ArcGisMigrationParityArtifact
        {
            SourceKind = manifest.SourceKind,
            Source = source,
            Classification = "pass",
            Reasons = ["probe"]
        };
        var parityJson = PostgresArcGisMigrationEvidenceStore.SerializeParity(parity);
        var parityRoundTrip = PostgresArcGisMigrationEvidenceStore.DeserializeParity(parityJson);
        if (parityRoundTrip is null
            || !string.Equals(parityRoundTrip.Classification, "pass", StringComparison.Ordinal)
            || parityRoundTrip.Reasons.Length != 1)
        {
            throw new InvalidOperationException($"Migration parity JSON did not round-trip: {parityJson}");
        }

        Console.WriteLine($"manifest-source={manifestRoundTrip.SourceKind}");
        Console.WriteLine($"parity-classification={parityRoundTrip.Classification}");
    }

    private static void WriteStacFilter()
    {
        using var extra = JsonDocument.Parse("{\"nested\":true}");
        var feature = Feature.Create(
            42,
            null,
            new Dictionary<string, object?>
            {
                ["name"] = "Honolulu",
                ["population"] = 350000L,
                ["active"] = true,
                ["score"] = 1.5m,
                ["extra"] = extra.RootElement.Clone()
            }.ToImmutableDictionary());
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "stac-probe", Name = "probe" },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.BigInteger, Nullable = true }
            ]
        };

        var bag = StacBoundItemQueryExecutor.BuildPropertyBag(feature, resource);
        var matches = InMemoryFilterEvaluator.Evaluate(
            new BinaryExpression(
                new PropertyReference("name"),
                BinaryOperator.Equal,
                new Literal("Honolulu", LiteralType.Text)),
            bag);
        var rejects = InMemoryFilterEvaluator.Evaluate(
            new BinaryExpression(
                new PropertyReference("population"),
                BinaryOperator.GreaterThan,
                new Literal(1_000_000L, LiteralType.Number)),
            bag);
        if (!matches
            || rejects
            || RequireProperty(bag["extra"], "nested").GetBoolean() != true
            || RequireProperty(bag, "objectid").GetInt64() != 42
            || RequireProperty(bag, "population").GetInt64() != 350000)
        {
            throw new InvalidOperationException("STAC ids+filter property bag did not preserve attribute values.");
        }

        Console.WriteLine("stac-filter=true");
        Console.WriteLine("stac-objectid=42");
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        var json = await reader.ReadToEndAsync();
        using var document = JsonDocument.Parse(json);
        return (http.Response.StatusCode, document.RootElement.Clone());
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new InvalidOperationException($"Missing JSON property '{name}' in {element.GetRawText()}");
    }

    private static JsonElement RequireProperty(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            throw new InvalidOperationException($"Missing STAC property '{name}'.");
        }

        return value;
    }

    private sealed class SeededProgressStore(IOperationProgress progress) : IUniversalProgressStore
    {
        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId,
            IOperationProgress progress,
            OperationStatus expectedStatus,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ProgressCompareAndSetResult.Updated);

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(progress as TProgress);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IOperationProgress?>(
                string.Equals(operationId, progress.OperationId, StringComparison.Ordinal) ? progress : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> ids = operationType is null || operationType == progress.Type
                ? [progress.OperationId]
                : [];
            return Task.FromResult(ids);
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(progress is TProgress typed ? [typed] : []);
    }
}
