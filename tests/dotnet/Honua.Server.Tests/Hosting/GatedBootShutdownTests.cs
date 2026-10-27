// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

[Protocol(TestProtocols.Infrastructure)]
public sealed class GatedBootShutdownTests
{
    [UnitTheory]
    [InlineData("Migration safety gate blocked pending contract-phase migration(s) on an existing database", true)]
    [InlineData("Migration safety gate blocked", true)]
    [InlineData("Database migration failed", false)]
    [InlineData("Unrelated failure: Migration safety gate blocked", false)]
    [InlineData("migration safety gate blocked", false)]
    [InlineData("", false)]
    public void IsContractGateRefusal_OnlyRecognizesTerminalGateMessage(string message, bool expected)
    {
        GatedBootShutdown.IsContractGateRefusal(new InvalidOperationException(message))
            .Should().Be(expected);
    }

    [UnitTest]
    public void IsContractGateRefusal_OtherExceptionTypesKeepTheirFailureBehavior()
    {
        GatedBootShutdown.IsContractGateRefusal(new IOException(GatedBootShutdown.ContractGateRefusalPrefix))
            .Should().BeFalse();
        GatedBootShutdown.IsContractGateRefusal(new AggregateException(
            new InvalidOperationException(GatedBootShutdown.ContractGateRefusalPrefix)))
            .Should().BeFalse();
    }

    [UnitTest]
    public void IsSchemaFloorRefusal_RecognizesTheFloorExceptionAndNotTheContractGate()
    {
        var floor = new DatabaseSchemaFloorException(
            "Honua.Postgres.Migrations.001_CreateRasterTables.sql",
            DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal,
            "the raster provider baseline migration is not recorded in public.schema_versions.");

        GatedBootShutdown.IsSchemaFloorRefusal(floor).Should().BeTrue();
        GatedBootShutdown.IsContractGateRefusal(floor).Should().BeFalse(
            "a schema-floor refusal is not the contract-approval gate");
        GatedBootShutdown.IsSchemaFloorRefusal(new InvalidOperationException(GatedBootShutdown.ContractGateRefusalPrefix))
            .Should().BeFalse();
        GatedBootShutdown.IsSchemaFloorRefusal(new InvalidOperationException(floor.Message))
            .Should().BeFalse("only the schema-floor exception type takes the floor shutdown path");
    }

    [UnitTest]
    public void IsAttachmentBindingRefusal_RecognizesTheProviderMismatchAndNotAPlainInvalidOperation()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Honua.Postgres.dll"));
        var type = assembly.GetType(GatedBootShutdown.AttachmentBindingRefusalTypeName, throwOnError: true)!;
        var mismatch = (Exception)Activator.CreateInstance(
            type,
            "Attachment family is absent despite its required migration phase.")!;

        GatedBootShutdown.IsAttachmentBindingRefusal(mismatch).Should().BeTrue();
        GatedBootShutdown.IsSchemaFloorRefusal(mismatch).Should().BeFalse();
        GatedBootShutdown.IsContractGateRefusal(mismatch).Should().BeFalse(
            "a binding mismatch is not the contract-approval gate, even though it derives from InvalidOperationException");
        GatedBootShutdown.IsAttachmentBindingRefusal(new InvalidOperationException(mismatch.Message))
            .Should().BeFalse("only the provider exception type takes the binding shutdown path");
    }
}
