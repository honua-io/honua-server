// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

/// <summary>
/// Projects only the two historical attachment scripts onto their resolved
/// physical authority. Embedded bytes and journal identities remain unchanged.
/// Other scripts retain their original contents and options.
/// </summary>
internal sealed class AttachmentSchemaScriptProvider(IScriptProvider source, string physicalSchema, Assembly contractAssembly) : IScriptProvider
{
    internal const string PreambleResourceName = "Honua.Server.MigrationContracts.attachment-schema-preamble.v1.sql.template";
    private const string PreambleResourceSha256 = "c5babe10e8028c1dcf9648f2ff4de80505ec9fb28176c9fcd4800d29108f01b3";
    private readonly string _quotedSchema = SchemaSearchPath.ValidateAndQuote(physicalSchema);
    private readonly PreambleContract? _preamble = LoadPreamble(contractAssembly, physicalSchema);

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => source.GetScripts(connectionManager).Select(Render);

    internal SqlScript Render(SqlScript script) => Render(script, _quotedSchema, _preamble);

    private static SqlScript Render(SqlScript script, string quotedSchema, PreambleContract? preamble)
    {
        var rendered = script.Name switch
        {
            PostgresAttachmentSchema.CreateMigration =>
                (preamble?.Sql ?? string.Empty) +
                script.Contents.Replace("honua.attachments", quotedSchema + ".attachments", StringComparison.Ordinal),
            PostgresAttachmentSchema.ImportMigration => script.Contents
                .Replace("$HonuaSchema$.attachments", quotedSchema + ".attachments", StringComparison.Ordinal)
                .Replace("$HonuaSchema$.import_attachment_cleanup", quotedSchema + ".import_attachment_cleanup", StringComparison.Ordinal),
            _ => script.Contents
        };

        return new SqlScript(script.Name, rendered, script.SqlScriptOptions);
    }

    internal static string SqlDigest(string contents)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();

    internal static IReadOnlyList<DatabaseMigrationScriptBinding> Describe(
        Assembly assembly, string configuredSchema, string physicalSchema)
    {
        var quotedSchema = SchemaSearchPath.ValidateAndQuote(physicalSchema);
        var preamble = LoadPreamble(assembly, physicalSchema);
        var result = new List<DatabaseMigrationScriptBinding>();
        foreach (var name in new[] { PostgresAttachmentSchema.CreateMigration, PostgresAttachmentSchema.ImportMigration })
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Canonical attachment migration resource is missing.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var raw = bytes.ToArray();
            using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var script = Render(new SqlScript(name, reader.ReadToEnd()), quotedSchema, preamble);
            result.Add(new DatabaseMigrationScriptBinding
            {
                ScriptName = name,
                ConfiguredSchema = configuredSchema,
                PhysicalSchema = physicalSchema,
                AttachmentsTable = SchemaSearchPath.QualifyTable("attachments", physicalSchema),
                ImportCleanupTable = SchemaSearchPath.QualifyTable("import_attachment_cleanup", physicalSchema),
                ResourceSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
                PreambleResourceName = name == PostgresAttachmentSchema.CreateMigration ? preamble?.ResourceName : null,
                PreambleResourceSha256 = name == PostgresAttachmentSchema.CreateMigration ? preamble?.ResourceSha256 : null,
                RenderedSqlSha256 = SqlDigest(script.Contents)
            });
        }

        return result;
    }

    private static PreambleContract? LoadPreamble(Assembly assembly, string physicalSchema)
    {
        if (physicalSchema == "honua")
        {
            return null;
        }

        var quoted = SchemaSearchPath.ValidateAndQuote(physicalSchema);
        using var stream = assembly.GetManifestResourceStream(PreambleResourceName)
            ?? throw new InvalidOperationException("Canonical attachment preamble contract is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var raw = bytes.ToArray();
        var digest = ValidatePreambleBytes(raw);
        using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new PreambleContract(PreambleResourceName, digest,
            reader.ReadToEnd().Replace("$PhysicalSchema$", quoted, StringComparison.Ordinal));
    }

    internal static string ValidatePreambleBytes(byte[] raw)
    {
        var digest = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (digest != PreambleResourceSha256)
        {
            throw new InvalidOperationException("Immutable attachment preamble contract bytes differ from v1.");
        }

        return digest;
    }

    private sealed record PreambleContract(string ResourceName, string ResourceSha256, string Sql);
}
