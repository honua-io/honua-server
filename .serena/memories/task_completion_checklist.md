# Task Completion Checklist

## Pre-Implementation Quality Checks

### Before Writing Code
- [ ] Read relevant GitHub issue and acceptance criteria
- [ ] Understand the current phase (Phase 0-5) and scope boundaries
- [ ] Check existing ADRs for architectural decisions
- [ ] Identify which protocol(s) the task affects (FeatureServer, OGC, OData, Admin)
- [ ] Plan the vertical slice approach (feature-first, not layer-first)

## Code Implementation Standards

### During Development
- [ ] **License Header**: All new C# files include copyright notice
- [ ] **Code Style**: Follow .editorconfig rules (PascalCase, camelCase, _privateFields)
- [ ] **AOT Compatibility**: No reflection, use source generators for JSON/logging
- [ ] **Dependency Limits**: Max 5 dependencies per endpoint, max 4 per handler
- [ ] **Error Handling**: Fail fast, validate early, no silent failures
- [ ] **Performance**: Avoid LINQ in hot paths, use Span<T>, consider object pooling
- [ ] **Nullable Safety**: Handle nullable reference types correctly

### Architecture Compliance
- [ ] **Vertical Slices**: Code organized by feature, not technical layer
- [ ] **Interface Segregation**: Small, focused interfaces
- [ ] **Single Responsibility**: Classes have one clear purpose
- [ ] **Immutable Patterns**: Use records, readonly, functional approaches where appropriate

## Testing Requirements

### Test Coverage
- [ ] **Integration Tests**: Primary focus - test full endpoint to database flow
- [ ] **Unit Tests**: For pure functions, business logic, parsers
- [ ] **Test Naming**: Follow `MethodUnderTest_Scenario_ExpectedBehavior` pattern
- [ ] **Test Attributes**: Use `[IntegrationTest]`, `[Protocol()]`, `[Operation()]` as appropriate
- [ ] **Data Isolation**: Tests use unique test data (no shared state conflicts)

### Test Quality
- [ ] Tests are fast (unit tests < 50ms, integration tests < 500ms)
- [ ] Tests are deterministic (no flaky tests)
- [ ] Tests verify behavior, not implementation details
- [ ] Error scenarios are tested (invalid input, edge cases)
- [ ] Protocol conformance tests added where applicable

## Quality Gates (Must Pass)

### Build & Format
```bash
# 1. Code formatting
dotnet format Honua.sln --verify-no-changes

# 2. Build with warnings as errors
dotnet build Honua.sln --configuration Release

# 3. Run tests with coverage
dotnet test --configuration Release --collect:"XPlat Code Coverage"
```

### Coverage Requirements
- [ ] **Line Coverage**: Maintain 80%+ overall
- [ ] **Branch Coverage**: Maintain 70%+ overall  
- [ ] **Critical Paths**: 95%+ coverage (query execution, transactions, auth)
- [ ] **New Code**: 90%+ coverage for files touched

### Performance Verification
- [ ] No performance regressions in benchmarks (if applicable)
- [ ] Memory usage stays bounded (no leaks in soak tests)
- [ ] Cold start time remains < 100ms (AOT builds)

## Documentation Updates

### Code Documentation
- [ ] **Public APIs**: XML documentation for public methods/classes
- [ ] **Complex Logic**: Code comments for non-obvious business rules
- [ ] **Configuration**: Document new environment variables or settings

### Project Documentation
- [ ] **README**: Update if new features/endpoints added
- [ ] **Architecture Docs**: Update if architectural changes made
- [ ] **ADR**: Create new ADR if significant design decision made
- [ ] **Issue Tracking**: Update GitHub issue with implementation notes

## Phase-Specific Requirements

### Phase 0 (Foundation)
- [ ] Infrastructure setup only
- [ ] No business logic implementation
- [ ] Focus on build/test/deploy pipeline

### Phase 1+ (Feature Implementation)
- [ ] **API Surface Coverage**: Every endpoint has at least one integration test
- [ ] **Error Response Format**: Matches protocol specifications (Esri, OGC, OData)
- [ ] **Input Validation**: Proper validation with clear error messages
- [ ] **Logging**: Structured logging using `[LoggerMessage]` source generators

## Security Checklist

### Input Validation
- [ ] **SQL Injection**: Parameterized queries only, no string concatenation
- [ ] **Input Sanitization**: Validate all user inputs (queries, file uploads)
- [ ] **Path Traversal**: Validate file paths for uploads/attachments
- [ ] **XSS Prevention**: Proper output encoding (admin UI)

### Authentication/Authorization (Phase 5+)
- [ ] **OIDC Integration**: Proper token validation
- [ ] **Admin Endpoints**: Protected with authentication
- [ ] **CORS**: Configured appropriately for admin UI

## Deployment Readiness

### Configuration
- [ ] **Environment Variables**: Use env vars for configuration, not hardcoded values
- [ ] **Secrets**: No secrets in code or config files
- [ ] **Database**: Connection string configurable via environment

### Container Compatibility
- [ ] **AOT Build**: Native AOT compilation succeeds without warnings
- [ ] **Container**: Runs as non-root user in read-only filesystem
- [ ] **Health Endpoints**: `/healthz/live` and `/healthz/ready` respond correctly

## Pre-Commit Final Steps

### Git Workflow
```bash
# 1. Ensure instruction parity
bash scripts/check-instructions-sync.sh

# 2. Stage all changes
git add .

# 3. Final quality check
dotnet format Honua.sln --verify-no-changes
dotnet build Honua.sln --configuration Release
dotnet test --configuration Release

# 4. Commit with conventional format
git commit -m "feat: add query endpoint (#12)

Implements Phase 1 FeatureServer query endpoint with:
- WHERE clause filtering
- Spatial predicates (bbox, intersects)  
- Result paging and field selection
- Esri JSON response format

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude Sonnet 4 <noreply@anthropic.com>"

# 5. Push to remote
git push
```

### GitHub PR Checklist
- [ ] **Issue Reference**: PR references the GitHub issue
- [ ] **Acceptance Criteria**: All issue acceptance criteria met
- [ ] **Breaking Changes**: None (or properly documented)
- [ ] **Migration Required**: Database migrations included if schema changed
- [ ] **Backward Compatibility**: APIs remain compatible within the same phase

## Phase Completion Criteria

Each phase has specific exit criteria that must be met:

### Phase 1 (FeatureServer Query)
- [ ] Query with WHERE clause returns filtered results
- [ ] Spatial queries (bbox, intersects) work correctly
- [ ] Paging returns correct pages with proper metadata
- [ ] Error responses match Esri format
- [ ] 80%+ coverage on query execution paths

### Phase 2-5
See `docs/MVP_PLAN.md` for detailed phase-specific exit criteria.

## When Task is Complete

### Final Verification
1. ✅ All quality gates pass
2. ✅ Tests provide adequate coverage  
3. ✅ Documentation updated
4. ✅ Security considerations addressed
5. ✅ Performance impact assessed
6. ✅ Phase exit criteria met (if applicable)

### Handoff
- Update GitHub issue with completion notes
- Tag relevant stakeholders for review
- Prepare for PR review with clear description
- Document any follow-up tasks or technical debt