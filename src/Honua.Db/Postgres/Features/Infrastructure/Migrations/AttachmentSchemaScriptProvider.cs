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
internal sealed class AttachmentSchemaScriptProvider(IScriptProvider source, string physicalSchema) : IScriptProvider
{
    private readonly string _quotedSchema = SchemaSearchPath.ValidateAndQuote(physicalSchema);

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => source.GetScripts(connectionManager).Select(Render);

    internal SqlScript Render(SqlScript script) => Render(script, _quotedSchema);

    private static SqlScript Render(SqlScript script, string quotedSchema)
    {
        var rendered = script.Name switch
        {
            PostgresAttachmentSchema.CreateMigration => script.Contents.Replace(
                "honua.attachments", quotedSchema + ".attachments", StringComparison.Ordinal),
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
        var result = new List<DatabaseMigrationScriptBinding>();
        foreach (var name in new[] { PostgresAttachmentSchema.CreateMigration, PostgresAttachmentSchema.ImportMigration })
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Canonical attachment migration resource is missing.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var raw = bytes.ToArray();
            using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var script = Render(new SqlScript(name, reader.ReadToEnd()), quotedSchema);
            result.Add(new DatabaseMigrationScriptBinding
            {
                ScriptName = name,
                ConfiguredSchema = configuredSchema,
                PhysicalSchema = physicalSchema,
                AttachmentsTable = SchemaSearchPath.QualifyTable("attachments", physicalSchema),
                ImportCleanupTable = SchemaSearchPath.QualifyTable("import_attachment_cleanup", physicalSchema),
                ResourceSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
                RenderedSqlSha256 = SqlDigest(script.Contents)
            });
        }

        return result;
    }

}
