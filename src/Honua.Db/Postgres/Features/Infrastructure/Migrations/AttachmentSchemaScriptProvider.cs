// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

/// <summary>
/// Projects only the two historical attachment scripts onto their resolved
/// physical authority. Embedded bytes and journal identities remain unchanged.
/// Other scripts retain their original contents and options.
/// </summary>
internal sealed class AttachmentSchemaScriptProvider(IScriptProvider source, string physicalSchema, Assembly contractAssembly, PostgresAttachmentMigrationContract contract) : IScriptProvider
{
    private readonly string _quotedSchema = SchemaSearchPath.ValidateAndQuote(physicalSchema);
    private readonly PreambleContract? _preamble = LoadPreamble(contractAssembly, physicalSchema, contract);

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => source.GetScripts(connectionManager).Select(Render);

    internal SqlScript Render(SqlScript script) => Render(script, _quotedSchema, _preamble, contract);

    private static SqlScript Render(SqlScript script, string quotedSchema, PreambleContract? preamble, PostgresAttachmentMigrationContract contract)
    {
        var rendered = script.Name switch
        {
            var name when name == contract.CreateMigration =>
                (preamble?.Sql ?? string.Empty) +
                script.Contents.Replace("honua.attachments", quotedSchema + ".attachments", StringComparison.Ordinal),
            var name when name == contract.ImportMigration => script.Contents
                .Replace("$HonuaSchema$.attachments", quotedSchema + ".attachments", StringComparison.Ordinal)
                .Replace("$HonuaSchema$.import_attachment_cleanup", quotedSchema + ".import_attachment_cleanup", StringComparison.Ordinal),
            _ => script.Contents
        };

        return new SqlScript(script.Name, rendered, script.SqlScriptOptions);
    }

    internal static string SqlDigest(string contents)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();

    internal static IReadOnlyList<DatabaseMigrationScriptBinding> Describe(
        Assembly assembly, string configuredSchema, string physicalSchema, PostgresAttachmentMigrationContract contract)
    {
        var quotedSchema = SchemaSearchPath.ValidateAndQuote(physicalSchema);
        var preamble = LoadPreamble(assembly, physicalSchema, contract);
        var result = new List<DatabaseMigrationScriptBinding>();
        foreach (var name in new[] { contract.CreateMigration, contract.ImportMigration })
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Canonical attachment migration resource is missing.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var raw = bytes.ToArray();
            using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var script = Render(new SqlScript(name, reader.ReadToEnd()), quotedSchema, preamble, contract);
            result.Add(new DatabaseMigrationScriptBinding
            {
                ScriptName = name,
                ConfiguredSchema = configuredSchema,
                PhysicalSchema = physicalSchema,
                AttachmentsTable = SchemaSearchPath.QualifyTable("attachments", physicalSchema),
                ImportCleanupTable = SchemaSearchPath.QualifyTable("import_attachment_cleanup", physicalSchema),
                ResourceSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
                PreambleResourceName = name == contract.CreateMigration ? preamble?.ResourceName : null,
                PreambleResourceSha256 = name == contract.CreateMigration ? preamble?.ResourceSha256 : null,
                RenderedSqlSha256 = SqlDigest(script.Contents)
            });
        }

        return result;
    }

    private static PreambleContract? LoadPreamble(Assembly assembly, string physicalSchema, PostgresAttachmentMigrationContract contract)
    {
        if (physicalSchema == "honua")
        {
            return null;
        }

        var quoted = SchemaSearchPath.ValidateAndQuote(physicalSchema);
        using var stream = assembly.GetManifestResourceStream(contract.PreambleResourceName)
            ?? throw new InvalidOperationException("Canonical attachment preamble contract is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var raw = bytes.ToArray();
        var digest = ValidatePreambleBytes(raw, contract);
        using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new PreambleContract(contract.PreambleResourceName, digest,
            reader.ReadToEnd().Replace("$PhysicalSchema$", quoted, StringComparison.Ordinal));
    }

    internal static string ValidatePreambleBytes(byte[] raw, PostgresAttachmentMigrationContract contract)
    {
        var digest = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (digest != contract.PreambleResourceSha256)
        {
            throw new InvalidOperationException("Immutable attachment preamble contract bytes differ from v1.");
        }

        return digest;
    }

    private sealed record PreambleContract(string ResourceName, string ResourceSha256, string Sql);
}
