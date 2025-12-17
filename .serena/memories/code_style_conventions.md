# Honua Server Code Style & Conventions

## Language & Framework Standards
- **Target Framework**: .NET 10 with Native AOT compilation
- **Language Version**: C# Preview features enabled
- **Nullable Reference Types**: Enabled globally (`<Nullable>enable</Nullable>`)
- **Implicit Usings**: Enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- **Warnings as Errors**: Enforced (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)

## File Headers & License
All C# files must include the license header:
```csharp
// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
```

## Code Style Rules

### Namespaces
- **File-scoped namespaces** required (`csharp_style_namespace_declarations = file_scoped:warning`)
- **Using directives** outside namespace (`csharp_using_directive_placement = outside_namespace:warning`)
- **System usings first** (`dotnet_sort_system_directives_first = true`)

### Naming Conventions
- **Classes, Methods, Properties**: PascalCase
- **Parameters, Local Variables**: camelCase  
- **Private Fields**: `_camelCase` with underscore prefix
- **Constants**: PascalCase
- **Static Readonly**: PascalCase
- **Interfaces**: IPascalCase (I prefix required)
- **Type Parameters**: TPascalCase (T prefix required)
- **Async Methods**: AsyncSuffix (suggestion only)

### Code Formatting
- **Indentation**: 4 spaces (no tabs)
- **Line Endings**: LF (Unix style)
- **Encoding**: UTF-8
- **Trim Trailing Whitespace**: Yes
- **Final Newline**: Required
- **Braces**: Allman style (new line for opening brace)

### Language Preferences
- **`var` Usage**: Only when type is apparent (`csharp_style_var_when_type_is_apparent = true`)
- **Built-in Types**: Prefer keywords (`int` over `Int32`)
- **Primary Constructors**: Preferred for C# 12 (`csharp_style_prefer_primary_constructors = true`)
- **Collection Expressions**: Preferred for C# 12 (`dotnet_style_prefer_collection_expression = true`)
- **File-scoped namespaces**: Required
- **Top-level statements**: Preferred (`csharp_style_prefer_top_level_statements = true`)

### Expression Preferences
- **Expression-bodied members**: Single line only
- **Pattern matching**: Preferred over is/as with casting
- **Switch expressions**: Preferred over switch statements
- **Null coalescing**: Required (`??` over null checks)
- **Null propagation**: Required (`?.` over null checks)

## AOT Compatibility Requirements
- **No Reflection**: Use source generators instead
- **No `dynamic` keyword**: Compile-time types only
- **No runtime code generation**: Source generators only
- **JSON Serialization**: Must use source-generated serializers
- **Logging**: Must use `[LoggerMessage]` source generators
- **DI Registration**: Explicit only (no assembly scanning)

## Architecture Patterns

### Vertical Slices
Organize by feature, not technical layer:
```
src/Honua.Server/Endpoints/
├── FeatureServer/     # GeoServices REST
├── OgcFeatures/       # OGC API Features  
└── Admin/             # Admin API
```

### Dependency Injection
- **Maximum Dependencies**: 5 per endpoint, 4 per handler
- **Explicit Registration**: No assembly scanning (AOT compatibility)
- **Interface Segregation**: Small, focused interfaces

### Error Handling
- **Fail Fast**: Validate early, throw on invalid state
- **No Silent Failures**: Always propagate or log errors
- **Structured Errors**: Consistent error response format

### Performance Patterns
- **Immutable by Default**: Records, readonly, functional patterns
- **Zero Allocations**: Use `Span<T>`, `stackalloc`, object pooling
- **No LINQ in Hot Paths**: Use `foreach` for performance-critical code
- **Streaming**: Large result sets should stream, not buffer

## Testing Conventions

### Test Naming
Pattern: `MethodUnderTest_Scenario_ExpectedBehavior`
```csharp
Query_WithWhereClause_ReturnsFilteredFeatures()
Query_InvalidSyntax_Returns400WithErrorDetails()
```

### Test Attributes
```csharp
[UnitTest]              // No I/O, runs in milliseconds
[IntegrationTest]       // Requires database
[SlowTest]             // CI only (30+ seconds)
[Protocol(Protocols.FeatureServer)]
[Operation(Operations.Query)]
[Conformance(Specs.EsriFeatureServer)]
```

### Test Organization
- **Integration-first**: 70% integration, 20% unit, 10% E2E
- **Real Database**: Testcontainers with PostgreSQL
- **Parallel Execution**: Tests isolated with unique data
- **Collection Fixtures**: Share expensive setup (database containers)

## Documentation Standards
- **XML Documentation**: Generate documentation files (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`)
- **Missing XML Doc Warnings**: Suppressed (`<NoWarn>CS1591</NoWarn>`)
- **Architecture Decision Records**: Document significant decisions in `docs/adr/`
- **README**: Keep current with actual implementation status

## Git Conventions
- **Commit Messages**: Conventional commits (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`, `ci:`)
- **Reference Issues**: Include GitHub issue number (`feat: add query endpoint (#12)`)
- **Atomic Commits**: Each commit should be focused and complete

## Performance Standards
- **Cold Start (AOT)**: < 100ms
- **Query p50**: < 50ms (100 features)
- **Query p99**: < 300ms (100 features)
- **Throughput**: > 1000 rps for simple queries
- **Memory**: No unbounded growth under sustained load