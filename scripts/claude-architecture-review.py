#!/usr/bin/env python3
"""
Local Claude Architecture Review for Honua Server
Run before creating PRs to catch architectural issues early
"""

import os
import sys
import subprocess
import json
from pathlib import Path
from typing import List, Dict, Any

def get_honua_rules() -> str:
    """Get Honua-specific architecture rules from CLAUDE.md"""
    try:
        claude_md_path = Path("CLAUDE.md")
        if claude_md_path.exists():
            with open(claude_md_path, 'r', encoding='utf-8') as f:
                content = f.read()

            # Extract Critical Rules section (same as CI does)
            import re
            match = re.search(r'## Critical Rules.*?(?=## Phase-Based Development)', content, re.DOTALL)
            if match:
                rules_section = match.group(0)
                return f"# Architecture Rules from CLAUDE.md\n\n{rules_section}"

        # Fallback if CLAUDE.md not found or section not found
        return """# Honua Architecture Rules (Fallback)

## Critical Rules

### Quality Standards
- **Warnings as errors**: All builds must pass with TreatWarningsAsErrors=true
- **Coverage gates**: 80%+ line coverage, 70%+ branch coverage
- **API surface coverage**: 100% - every endpoint must have integration tests
- **AOT compatibility**: No reflection in hot paths, source-generated JSON/logging
- **Dependency limits**: Max 5 dependencies per endpoint, max 4 per handler
- **Code formatting**: Always run dotnet format before PRs

### Architecture
- **Vertical slices**: Organize by feature, not layer
- **Composition over inheritance**: Small focused classes
- **Integration-first testing**: Real database in tests, minimal mocking
"""
    except Exception as e:
        return f"# Architecture Rules (Error reading CLAUDE.md: {e})\n\nFallback to basic checks..."

def get_changed_files(base_ref: str = "main") -> List[str]:
    """Get list of changed C# files since base ref"""
    try:
        # Get changed files
        result = subprocess.run([
            'git', 'diff', '--name-only', f"{base_ref}...HEAD"
        ], capture_output=True, text=True, check=True)

        files = [f for f in result.stdout.strip().split('\n')
                if f.endswith('.cs') and f.strip()]
        return files
    except subprocess.CalledProcessError:
        return []

def get_file_content_and_diff(file_path: str, base_ref: str = "main") -> Dict[str, str]:
    """Get file content and diff for review"""
    content = ""
    diff = ""

    try:
        # Get current file content
        if os.path.exists(file_path):
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()

        # Get diff
        result = subprocess.run([
            'git', 'diff', f"{base_ref}...HEAD", '--', file_path
        ], capture_output=True, text=True, check=True)
        diff = result.stdout

    except Exception as e:
        print(f"Warning: Could not process {file_path}: {e}")

    return {"content": content, "diff": diff}

def analyze_dependencies(file_path: str, content: str) -> List[str]:
    """Check for dependency violations"""
    violations = []

    # Check if Core project depends on Infrastructure
    if "Honua.Core" in file_path:
        if "using Honua.Postgres" in content:
            violations.append(f"❌ BLOCKING: Core depends on Infrastructure (Honua.Postgres)")
        if "using Honua.Server" in content:
            violations.append(f"❌ BLOCKING: Core depends on Server")

    return violations

def analyze_api_patterns(file_path: str, content: str) -> List[str]:
    """Check for API pattern violations"""
    violations = []

    # Check for Controller usage
    if ": ControllerBase" in content or ": Controller" in content:
        violations.append(f"❌ BLOCKING: Controller usage found - use Minimal APIs instead")

    # Check for public repository types (security issue)
    if "public class" in content and ("Repository" in content or "DataAccess" in content):
        violations.append(f"❌ BLOCKING: Public repository/data access type - should be internal")

    return violations

def analyze_documentation(file_path: str, content: str) -> List[str]:
    """Check for missing documentation"""
    violations = []

    # Check for public types without XML docs
    lines = content.split('\n')
    for i, line in enumerate(lines):
        if "public class" in line or "public interface" in line or "public enum" in line:
            # Check if previous line has XML documentation
            prev_line = lines[i-1] if i > 0 else ""
            if not prev_line.strip().startswith("///"):
                type_name = line.split()[-1] if line.split() else "unknown"
                violations.append(f"❌ BLOCKING: Missing XML documentation for public type: {type_name}")

    return violations

def analyze_design_patterns(file_path: str, content: str) -> List[str]:
    """Check for design pattern issues"""
    issues = []

    # Check for sync-over-async patterns
    if ".Result" in content or ".Wait()" in content:
        issues.append(f"⚠️  WARNING: Sync-over-async pattern detected - use await instead")

    # Check for deep inheritance (simple check)
    if ": " in content and content.count(": ") > 2:
        issues.append(f"⚠️  WARNING: Possible deep inheritance - consider composition")

    return issues

def local_architecture_review() -> Dict[str, Any]:
    """Perform local architecture review"""
    print("🔍 Running Claude Architecture Review...")

    changed_files = get_changed_files()

    if not changed_files:
        return {
            "status": "no_changes",
            "message": "No C# files changed - architecture review skipped"
        }

    print(f"📁 Found {len(changed_files)} changed C# files")

    all_violations = []
    all_warnings = []
    all_good_patterns = []

    for file_path in changed_files:
        print(f"  🔍 Analyzing {file_path}...")

        file_info = get_file_content_and_diff(file_path)
        content = file_info["content"]

        if not content:
            continue

        # Run checks
        dep_violations = analyze_dependencies(file_path, content)
        api_violations = analyze_api_patterns(file_path, content)
        doc_violations = analyze_documentation(file_path, content)
        design_issues = analyze_design_patterns(file_path, content)

        all_violations.extend(dep_violations + api_violations + doc_violations)
        all_warnings.extend(design_issues)

        # Note good patterns
        if "async Task" in content:
            all_good_patterns.append(f"✅ Good async patterns in {file_path}")
        if "internal class" in content and "Repository" in content:
            all_good_patterns.append(f"✅ Proper encapsulation in {file_path}")

    # Determine assessment
    if all_violations:
        assessment = "BLOCKING_ISSUES"
    elif all_warnings:
        assessment = "NEEDS_ATTENTION"
    else:
        assessment = "APPROVED"

    return {
        "status": "reviewed",
        "assessment": assessment,
        "violations": all_violations,
        "warnings": all_warnings,
        "good_patterns": all_good_patterns,
        "files_reviewed": len(changed_files)
    }

def print_review_results(results: Dict[str, Any]):
    """Print formatted review results"""
    if results["status"] == "no_changes":
        print(f"✅ {results['message']}")
        return True

    print("\n" + "="*60)
    print("🏗️  CLAUDE ARCHITECTURE REVIEW RESULTS")
    print("="*60)

    print(f"\n📊 Files Reviewed: {results['files_reviewed']}")
    print(f"🎯 Assessment: {results['assessment']}")

    if results["good_patterns"]:
        print("\n✅ GOOD PATTERNS FOUND:")
        for pattern in results["good_patterns"]:
            print(f"  {pattern}")

    if results["violations"]:
        print("\n❌ BLOCKING VIOLATIONS:")
        for violation in results["violations"]:
            print(f"  {violation}")

    if results["warnings"]:
        print("\n⚠️  ARCHITECTURE CONCERNS:")
        for warning in results["warnings"]:
            print(f"  {warning}")

    print("\n" + "="*60)

    if results["assessment"] == "BLOCKING_ISSUES":
        print("🚫 MUST FIX VIOLATIONS BEFORE CREATING PR")
        print("   CI will block this PR if violations remain")
        return False
    elif results["assessment"] == "NEEDS_ATTENTION":
        print("⚠️  CONSIDER ADDRESSING CONCERNS BEFORE PR")
        print("   These won't block CI but improve code quality")
        return True
    else:
        print("✅ ARCHITECTURE REVIEW PASSED")
        print("   Ready for PR creation")
        return True

def main():
    """Main function"""
    try:
        results = local_architecture_review()
        success = print_review_results(results)

        # Exit with error code if blocking issues found
        if not success:
            sys.exit(1)

    except KeyboardInterrupt:
        print("\n⚠️  Review interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"❌ Error during review: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()