// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.UnitTests.Features.Import;

public sealed class ImportCommittedResultCodecTests
{
    [Fact]
    public void CompleteResult_RoundTripsOriginalWarningsAndRowDiagnostics()
    {
        var result = Result() with
        {
            Warnings = ["One geometry repaired"], RepairedGeometryCount = 1,
            ValidationErrors = [ImportValidationIssue.Create("repaired", "Ring closed", 7, "geometry",
                ImportValidationSeverity.Warning)], Duration = TimeSpan.FromMilliseconds(1234)
        };
        ImportCommittedResultCodec.Decode(ImportCommittedResultCodec.Encode(result)).Should().BeEquivalentTo(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedDiagnosticOrEscapedJson_IsRefusedBeforeCommit(bool escaped)
    {
        var warning = new string(escaped ? '\u0001' : 'a', escaped ? 200000 : 1048577);
        var write = () => ImportCommittedResultCodec.Encode(Result() with { Warnings = [warning] });
        write.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("no-rows")]
    [InlineData("missing-destination")]
    public void NonCommittedResult_IsRefused(string mutation)
    {
        var result = mutation switch
        {
            "failed" => Result() with { Success = false },
            "no-rows" => Result() with { FeatureCount = 0 },
            _ => Result() with { PhysicalTableName = null }
        };
        var write = () => ImportCommittedResultCodec.Encode(result);
        write.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void UnknownReceiptVersion_IsRefused()
    {
        var original = Encoding.UTF8.GetString(ImportCommittedResultCodec.Encode(Result()));
        var read = () => ImportCommittedResultCodec.Decode(Encoding.UTF8.GetBytes(
            original.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal)));
        read.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void UnknownEnvelopeField_IsRefused()
    {
        var original = Encoding.UTF8.GetString(ImportCommittedResultCodec.Encode(Result()));
        var read = () => ImportCommittedResultCodec.Decode(Encoding.UTF8.GetBytes("{\"unexpected\":true," + original[1..]));
        read.Should().Throw<JsonException>();
    }

    private static ImportResult Result() => ImportResult.CreateSuccess("roads", SupportedFileFormat.GeoJson,
        2, 4326, physicalTableName: "imported_roads", schema: "honua_data");
}
