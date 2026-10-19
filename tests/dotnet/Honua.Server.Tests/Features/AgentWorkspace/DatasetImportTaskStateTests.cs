// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportTaskStateTests
{
    private static CreateDatasetImportTask Request() => new("intent-1", "tab-1", 0,
        new("public", "roads"), new("browser-file", "roads.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)), new());

    [UnitTest]
    public void TypedIntentPinsSourceDestinationOptionsAndOriginalContext()
    {
        var original = Request();
        DatasetImportTaskState.Validate(original);
        var hash = DatasetImportTaskState.RequestHash(original);
        DatasetImportTaskState.RequestHash(original with { Destination = new("public", "buildings") }).Should().NotBe(hash);
        DatasetImportTaskState.RequestHash(original with { Source = original.Source with { ExpectedSha256 = new string('b', 64) } }).Should().NotBe(hash);
        DatasetImportTaskState.RequestHash(original with { Options = new(ImportLoadMode.Replace) }).Should().NotBe(hash);
        DatasetImportTaskState.RequestHash(original with { ContextRevision = 1 }).Should().NotBe(hash);
        DatasetImportTaskState.IntentKey("actor-1", "intent-1").Should().NotBe(DatasetImportTaskState.IntentKey("actor-2", "intent-1"));
    }

    [UnitTest]
    public void TaskHashCoversAuthorityDeadlineAndImmutableInputsAndRoundTrips()
    {
        var request = Request();
        var task = new DatasetImportTask("import-task-1", 1, "", "actor-1", "tenant-1", "target-1", "workspace-1",
            request.Destination, request.Source, request.Options, DateTimeOffset.Parse("2026-10-19T00:00:00Z", CultureInfo.InvariantCulture));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(task, AgentWorkspaceJsonContext.Default.DatasetImportTask), AgentWorkspaceJsonContext.Default.DatasetImportTask)!;
        DatasetImportTaskState.ContentHash(restored).Should().Be(task.ContentHash);
        DatasetImportTaskState.ContentHash(task with { OwnerActorId = "actor-2" }).Should().NotBe(task.ContentHash);
        DatasetImportTaskState.ContentHash(task with { UploadAdmissionDeadline = task.UploadAdmissionDeadline.AddMinutes(1) }).Should().NotBe(task.ContentHash);
        AgentWorkspaceState.IsValidTaskReference(new(task.TaskId, 1, "dataset-import", ContentHash: task.ContentHash)).Should().BeTrue();
        AgentWorkspaceState.IsValidTaskReference(new(task.TaskId, 0, "dataset-import", ContentHash: task.ContentHash)).Should().BeFalse();
    }

    [UnitTest]
    public void RejectsUnsupportedUpsertUnboundArtifactPathsAndUnknownConnectionFields()
    {
        var upsert = () => DatasetImportTaskState.Validate(Request() with { Options = new(ImportLoadMode.Upsert) });
        upsert.Should().Throw<ArgumentException>();
        var artifact = () => DatasetImportTaskState.Validate(Request() with { Source = Request().Source with { Kind = "local-process-artifact" } });
        artifact.Should().Throw<ArgumentException>();
        var json = """{"schema":"public","table":"roads","connectionId":"foreign"}""";
        var unknown = () => JsonSerializer.Deserialize(json, AgentWorkspaceJsonContext.Default.DatasetImportDestination);
        unknown.Should().Throw<JsonException>();
        var invalid = () => DatasetImportTaskState.Validate(Request() with { Destination = new("public", "roads\n") });
        invalid.Should().Throw<ArgumentException>();
    }
}
