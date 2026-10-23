# PostgreSQL schema identifier model

This pack models only the return value of
`SchemaSearchPath.ValidateAndQuote(string)` as a SQL-injection barrier. That
method accepts one to 63 ASCII identifier characters, requires an ASCII letter
or underscore first, rejects the entire input on a mismatch, and quotes the
accepted identifier with `NpgsqlCommandBuilder.QuoteIdentifier`. It neither
accepts SQL expressions nor sanitizes arbitrary query text.

`SchemaSearchPathTests` verifies identifier quoting, injection rejection,
absolute anchoring, and PostgreSQL's identifier length boundary.
`SchemaSearchPathIntegrationTests` verifies that rejected inputs cannot change
the actual database search path. Keep this model synchronized with that
method's validation and quoting contract.

The model does not exclude request sources, commands, filters, ordering, or
query files. Raw input that bypasses this method remains subject to the normal
SQL-injection query. GitHub automatically loads repository model packs from
`.github/codeql/extensions`, including in advanced setup.

References: [C# barrier models](https://codeql.github.com/docs/codeql-language-guides/customizing-library-models-for-csharp/)
and [repository model packs](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/manage-your-configuration/edit-default-setup#extending-coverage-for-a-repository).
