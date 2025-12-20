#!/usr/bin/env python3
"""
Enhanced Local Claude Architecture Review for Honua Server
Implements specific pattern detection based on improved architecture guidance
"""

import os
import sys
import subprocess
import json
import re
from pathlib import Path
from typing import List, Dict, Any, Tuple

def get_honua_rules() -> str:
    """Get Honua-specific architecture rules from CLAUDE.md"""
    try:
        claude_md_path = Path("CLAUDE.md")
        if claude_md_path.exists():
            with open(claude_md_path, 'r', encoding='utf-8') as f:
                content = f.read()

            # Extract Critical Rules section
            match = re.search(r'## Critical Rules.*?(?=## Phase-Based Development)', content, re.DOTALL)
            if match:
                rules_section = match.group(0)
                return f"# Architecture Rules from CLAUDE.md\n\n{rules_section}"

        return """# Honua Architecture Rules (Fallback)
## Critical Rules - Minimal enforcement when CLAUDE.md unavailable"""

    except Exception as e:
        return f"# Architecture Rules (Error reading CLAUDE.md: {e})"

def get_changed_files(base_ref: str = "main") -> List[str]:
    """Get list of changed C# files since base ref"""
    try:
        result = subprocess.run([
            'git', 'diff', '--name-only', f"{base_ref}...HEAD"
        ], capture_output=True, text=True, check=True)

        files = [f for f in result.stdout.strip().split('\n')
                if f.endswith('.cs') and f.strip()]
        return files
    except subprocess.CalledProcessError:
        return []

def get_all_cs_files() -> List[str]:
    """Get all tracked C# files in the repo"""
    try:
        result = subprocess.run(
            ['git', 'ls-files', '*.cs'],
            capture_output=True,
            text=True,
            check=True,
        )
        files = [f for f in result.stdout.strip().split('\n') if f.strip()]
        return files
    except subprocess.CalledProcessError:
        # Fallback to walking the workspace
        cs_files: List[str] = []
        for root, _, filenames in os.walk("."):
            for name in filenames:
                if name.endswith(".cs"):
                    cs_files.append(os.path.join(root, name))
        return cs_files

def get_file_content_and_diff(file_path: str, base_ref: str = "main") -> Dict[str, str]:
    """Get file content and diff for review"""
    content = ""
    diff = ""

    try:
        if os.path.exists(file_path):
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()

    except Exception as e:
        print(f"Warning: Could not process {file_path}: {e}")

    return {"content": content, "diff": diff}

def detect_dependency_violations(file_path: str, content: str) -> List[Tuple[str, str]]:
    """Detect dependency direction violations with specific line references"""
    violations = []

    # Check Core layer dependency violations
    if "Honua.Core" in file_path:
        lines = content.split('\n')
        for line_num, line in enumerate(lines, 1):
            if "using Honua.Postgres" in line:
                violations.append((
                    "BLOCKING",
                    f"Core depends on Infrastructure at {file_path}:{line_num} - '{line.strip()}'"
                ))
            elif "using Honua.Server" in line:
                violations.append((
                    "BLOCKING",
                    f"Core depends on Server at {file_path}:{line_num} - '{line.strip()}'"
                ))

    # Check for circular dependencies
    if "Honua.Postgres" in file_path:
        lines = content.split('\n')
        for line_num, line in enumerate(lines, 1):
            if "using Honua.Server" in line:
                violations.append((
                    "BLOCKING",
                    f"Postgres depends on Server at {file_path}:{line_num} - '{line.strip()}'"
                ))

    return violations

def detect_api_pattern_violations(file_path: str, content: str) -> List[Tuple[str, str]]:
    """Detect API pattern violations (controllers, etc.)"""
    violations = []
    lines = content.split('\n')

    for line_num, line in enumerate(lines, 1):
        # Check for Controller usage
        if (": ControllerBase" in line or
            ": Controller" in line or
            (re.search(r'public class \w*Controller', line))):
            violations.append((
                "BLOCKING",
                f"Controller usage at {file_path}:{line_num} - '{line.strip()}' (Use Minimal APIs)"
            ))

    return violations

def detect_encapsulation_violations(file_path: str, content: str) -> List[Tuple[str, str]]:
    """Detect improper public exposure of infrastructure types"""
    violations = []
    lines = content.split('\n')

    # Check for public infrastructure types
    if ("Honua.Postgres" in file_path or
        "Repository" in file_path.lower() or
        "DataAccess" in file_path.lower()):

        for line_num, line in enumerate(lines, 1):
            # Look for public class declarations (but allow interfaces)
            if (re.search(r'public class', line) and
                not "interface" in line.lower()):
                violations.append((
                    "BLOCKING",
                    f"Public infrastructure type at {file_path}:{line_num} - '{line.strip()}' (Should be internal)"
                ))

    return violations

def detect_documentation_violations(file_path: str, content: str) -> List[Tuple[str, str]]:
    """Detect missing XML documentation on public APIs"""
    violations = []
    lines = content.split('\n')

    for idx, line in enumerate(lines):
        type_match = re.search(r'public\s+(class|interface|enum|struct|record)\s+(\w+)', line)
        if not type_match:
            continue

        line_num = idx + 1
        type_name = type_match.group(2)

        # Walk backward to find the first non-blank, non-attribute line
        has_doc = False
        check_idx = idx - 1
        while check_idx >= 0:
            prior = lines[check_idx].strip()
            if prior == "":
                check_idx -= 1
                continue
            if prior.startswith("["):
                check_idx -= 1
                continue
            if prior.startswith("///"):
                has_doc = True
            break

        if not has_doc:
            violations.append((
                "BLOCKING",
                f"Missing XML documentation for public type '{type_name}' at {file_path}:{line_num}"
            ))

    return violations

def detect_complexity_violations(file_path: str, content: str) -> List[Tuple[str, str]]:
    """Detect complexity issues (too many dependencies, etc.)"""
    violations = []
    lines = content.split('\n')

    for idx, line in enumerate(lines):
        class_match = re.search(r'\b(public|internal)\s+class\s+(\w+)(.*)', line)
        if not class_match:
            continue

        class_name = class_match.group(2)
        remainder = class_match.group(3)

        is_endpoint = "Endpoint" in class_name or "Endpoints" in class_name
        is_handler = "Handler" in class_name
        limit = 5 if is_endpoint else 4 if is_handler else None
        if limit is None:
            continue

        param_str = None
        param_line_num = idx + 1

        # Primary constructor on class declaration
        if "(" in remainder and ")" in remainder:
            paren_match = re.search(r'\(([^)]*)\)', remainder)
            if paren_match:
                param_str = paren_match.group(1)
        else:
            # Look ahead for a constructor
            for look_ahead in range(idx + 1, min(idx + 20, len(lines))):
                ctor_line = lines[look_ahead]
                ctor_match = re.search(rf'\bpublic\s+{re.escape(class_name)}\s*\(([^)]*)\)', ctor_line)
                if ctor_match:
                    param_str = ctor_match.group(1)
                    param_line_num = look_ahead + 1
                    break

        if param_str is None:
            continue

        param_count = len([p for p in param_str.split(',') if p.strip()])
        if param_count > limit:
            violations.append((
                "WARNING",
                f"Too many dependencies ({param_count}>{limit}) at {file_path}:{param_line_num}"
            ))

    return violations

def detect_performance_antipatterns(file_path: str, content: str) -> List[Tuple[str, str]]:
    """Detect performance anti-patterns"""
    violations = []
    lines = content.split('\n')

    for line_num, line in enumerate(lines, 1):
        # Check for sync-over-async patterns
        if (".Result" in line or ".Wait()" in line):
            violations.append((
                "WARNING",
                f"Sync-over-async pattern at {file_path}:{line_num} - '{line.strip()}' (Use await)"
            ))

    return violations

def detect_organizational_issues(file_path: str) -> List[Tuple[str, str]]:
    """Detect organizational anti-patterns"""
    violations = []

    # Check for layer-based organization (rough heuristic)
    path_parts = file_path.lower().split('/')
    layer_indicators = ['controllers', 'services', 'models', 'repositories', 'data']

    for indicator in layer_indicators:
        if indicator in path_parts and "features" not in path_parts:
            violations.append((
                "WARNING",
                f"Layer-based organization detected: {file_path} (Consider vertical slice organization)"
            ))
            break

    return violations

def check_positive_patterns(file_path: str, content: str, is_changed: bool) -> List[str]:
    """Identify positive architectural patterns"""
    if not is_changed:
        return []

    patterns = []

    # Check for proper async usage
    if "async Task" in content and ".Result" not in content and ".Wait()" not in content:
        patterns.append(f"✅ Proper async patterns in {file_path}")

    # Check for proper encapsulation
    if "internal class" in content and ("Repository" in content or "Store" in content):
        patterns.append(f"✅ Proper encapsulation of infrastructure in {file_path}")

    # Check for interface usage
    if "public interface" in content:
        patterns.append(f"✅ Good abstraction design in {file_path}")

    # Check for vertical slice organization
    if "/Features/" in file_path and len([p for p in file_path.split('/') if p.endswith('Endpoints.cs')]) > 0:
        patterns.append(f"✅ Vertical slice organization in {file_path}")

    # Check for proper testing attributes
    if ("[IntegrationTest]" in content and "[Protocol(" in content and "[Operation(" in content):
        patterns.append(f"✅ Comprehensive test attributes in {file_path}")

    return patterns

def analyze_file_architecture(file_path: str, content: str, is_changed: bool) -> Dict[str, Any]:
    """Comprehensive analysis of a single file"""
    analysis = {
        "blocking_violations": [],
        "warning_violations": [],
        "positive_patterns": []
    }

    # Run all violation checks
    all_checks = [
        detect_dependency_violations(file_path, content),
        detect_api_pattern_violations(file_path, content),
        detect_encapsulation_violations(file_path, content),
        detect_documentation_violations(file_path, content),
        detect_complexity_violations(file_path, content),
        detect_performance_antipatterns(file_path, content),
        detect_organizational_issues(file_path)
    ]

    # Categorize results
    for check_results in all_checks:
        for severity, violation in check_results:
            if severity == "BLOCKING":
                analysis["blocking_violations"].append(violation)
            elif severity == "WARNING":
                analysis["warning_violations"].append(violation)

    # Check for positive patterns
    analysis["positive_patterns"] = check_positive_patterns(file_path, content, is_changed)

    return analysis

def enhanced_architecture_review() -> Dict[str, Any]:
    """Perform enhanced architecture review with specific pattern detection"""
    print("🔍 Running Enhanced Claude Architecture Review...")

    changed_files = get_changed_files()
    changed_set = set(changed_files)
    all_files = get_all_cs_files()

    if not all_files:
        return {
            "status": "no_files",
            "message": "No C# files found for architecture review"
        }

    print(f"📁 Found {len(all_files)} C# files ({len(changed_files)} changed vs base)")

    all_blocking = []
    all_warnings = []
    all_positive = []

    for file_path in all_files:
        is_changed = file_path in changed_set
        label = "changed" if is_changed else "base"
        print(f"  🔍 Analyzing {file_path} ({label})...")

        file_info = get_file_content_and_diff(file_path)
        content = file_info["content"]

        if not content:
            continue

        # Analyze this file
        analysis = analyze_file_architecture(file_path, content, is_changed)

        tag = "[CHANGED]" if is_changed else "[BASE]"

        all_blocking.extend([f"{tag} {v}" for v in analysis["blocking_violations"]])
        all_warnings.extend([f"{tag} {v}" for v in analysis["warning_violations"]])
        all_positive.extend([f"{tag} {v}" for v in analysis["positive_patterns"]])

    # Determine overall assessment
    if all_blocking:
        assessment = "BLOCKING_ISSUES"
    elif all_warnings:
        assessment = "NEEDS_ATTENTION"
    else:
        assessment = "APPROVED"

    return {
        "status": "reviewed",
        "assessment": assessment,
        "blocking_violations": all_blocking,
        "warning_violations": all_warnings,
        "positive_patterns": all_positive,
        "files_reviewed": len(all_files),
        "changed_files": len(changed_files)
    }

def print_enhanced_results(results: Dict[str, Any]) -> bool:
    """Print formatted enhanced review results"""
    if results["status"] == "no_changes":
        print(f"✅ {results['message']}")
        return True
    if results["status"] == "no_files":
        print(f"⚠️  {results['message']}")
        return False

    print("\n" + "="*70)
    print("🏗️  ENHANCED CLAUDE ARCHITECTURE REVIEW")
    print("="*70)

    print(f"\n📊 Files Reviewed: {results['files_reviewed']} (changed: {results.get('changed_files', 0)})")
    print(f"🎯 Assessment: {results['assessment']}")

    if results["positive_patterns"]:
        print("\n✅ POSITIVE PATTERNS FOUND:")
        for pattern in results["positive_patterns"]:
            print(f"  {pattern}")

    if results["blocking_violations"]:
        print("\n❌ BLOCKING VIOLATIONS (Must Fix Before PR):")
        for violation in results["blocking_violations"]:
            print(f"  {violation}")

    if results["warning_violations"]:
        print("\n⚠️  WARNING VIOLATIONS (Review Recommended):")
        for warning in results["warning_violations"]:
            print(f"  {warning}")

    print("\n" + "="*70)

    if results["assessment"] == "BLOCKING_ISSUES":
        print("🚫 MUST FIX BLOCKING VIOLATIONS BEFORE CREATING PR")
        print("   These violations will be caught by CI architecture review")
        return False
    elif results["assessment"] == "NEEDS_ATTENTION":
        print("⚠️  CONSIDER ADDRESSING WARNING ISSUES")
        print("   These won't block CI but improve code quality")
        return True
    else:
        print("✅ ENHANCED ARCHITECTURE REVIEW PASSED")
        print("   Ready for PR - all checks passed")
        return True

def main():
    """Main function"""
    try:
        results = enhanced_architecture_review()
        success = print_enhanced_results(results)

        # Exit with error code if blocking issues found
        if not success:
            sys.exit(1)

    except KeyboardInterrupt:
        print("\n⚠️  Review interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"❌ Error during enhanced review: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()
