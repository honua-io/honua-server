#!/usr/bin/env python3
"""
Honua Architecture Review Script
Analyzes code changes using LLM for architectural compliance

Features:
- Complete .cs file analysis for full architectural context
- Honua-specific criteria (dependency flow, API patterns, documentation)
- Educational feedback explaining WHY patterns matter for geospatial/AOT
- Three-tier assessment: APPROVED/NEEDS_ATTENTION/BLOCKING_ISSUES
"""

import os
import sys
import json
import subprocess
import re
import urllib.request
import urllib.error
from pathlib import Path
from typing import List, Dict, Any, Optional

def get_pr_info() -> Dict[str, Any]:
    """Get PR information from GitHub API"""
    pr_number = os.environ.get('GITHUB_PR_NUMBER')
    repo = os.environ.get('GITHUB_REPOSITORY', 'honua/server')
    github_token = os.environ.get('GITHUB_TOKEN')

    if not pr_number or not github_token:
        return {"error": "Missing PR number or GitHub token", "linked_issues": [], "body": ""}

    try:
        url = f"https://api.github.com/repos/{repo}/pulls/{pr_number}"
        req = urllib.request.Request(url)
        req.add_header('Authorization', f'token {github_token}')
        req.add_header('Accept', 'application/vnd.github.v3+json')

        with urllib.request.urlopen(req) as response:
            pr_data = json.loads(response.read().decode())

        # Extract linked issues from PR body and title
        linked_issues = extract_linked_issues(pr_data.get('body', ''), pr_data.get('title', ''))

        return {
            "title": pr_data.get('title', ''),
            "body": pr_data.get('body', ''),
            "linked_issues": linked_issues,
            "number": pr_number
        }
    except Exception as e:
        return {"error": f"Failed to fetch PR info: {str(e)}", "linked_issues": [], "body": ""}

def extract_linked_issues(body: str, title: str) -> List[Dict[str, Any]]:
    """Extract GitHub issue references from PR body and title"""
    issue_patterns = [
        r'(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)',  # Closes #123
        r'(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(?:https://github\.com/[^/]+/[^/]+/)?issues/(\d+)',  # Full URL
        r'#(\d+)',  # Simple #123 reference
    ]

    text = f"{title} {body}".lower()
    issue_numbers = set()

    for pattern in issue_patterns:
        matches = re.findall(pattern, text, re.IGNORECASE)
        issue_numbers.update(matches)

    issues = []
    for issue_num in issue_numbers:
        issue_info = get_issue_details(issue_num)
        if issue_info:
            issues.append(issue_info)

    return issues

def get_issue_details(issue_number: str) -> Optional[Dict[str, Any]]:
    """Get issue details from GitHub API"""
    repo = os.environ.get('GITHUB_REPOSITORY', 'honua/server')
    github_token = os.environ.get('GITHUB_TOKEN')

    if not github_token:
        return None

    try:
        url = f"https://api.github.com/repos/{repo}/issues/{issue_number}"
        req = urllib.request.Request(url)
        req.add_header('Authorization', f'token {github_token}')
        req.add_header('Accept', 'application/vnd.github.v3+json')

        with urllib.request.urlopen(req) as response:
            issue_data = json.loads(response.read().decode())

        # Extract acceptance criteria
        acceptance_criteria = extract_acceptance_criteria(issue_data.get('body', ''))

        return {
            "number": issue_number,
            "title": issue_data.get('title', ''),
            "body": issue_data.get('body', ''),
            "acceptance_criteria": acceptance_criteria,
            "labels": [label['name'] for label in issue_data.get('labels', [])]
        }
    except Exception as e:
        return {"error": f"Failed to fetch issue {issue_number}: {str(e)}"}

def extract_acceptance_criteria(issue_body: str) -> List[str]:
    """Extract acceptance criteria from issue body"""
    if not issue_body:
        return []

    criteria = []

    # Look for "Acceptance Criteria" section
    ac_patterns = [
        r'## Acceptance Criteria\s*\n(.*?)(?=\n##|\n---|\Z)',
        r'### Acceptance Criteria\s*\n(.*?)(?=\n###|\n##|\n---|\Z)',
        r'Acceptance Criteria:?\s*\n(.*?)(?=\n##|\n---|\Z)',
    ]

    for pattern in ac_patterns:
        match = re.search(pattern, issue_body, re.DOTALL | re.IGNORECASE)
        if match:
            ac_text = match.group(1).strip()
            # Extract bullet points or checklist items
            lines = ac_text.split('\n')
            for line in lines:
                line = line.strip()
                if line and (line.startswith('- ') or line.startswith('* ') or re.match(r'^\d+\.', line) or line.startswith('[ ]') or line.startswith('[x]')):
                    # Clean up the line
                    clean_line = re.sub(r'^[-\*\d\.\[\]x\s]+', '', line).strip()
                    if clean_line:
                        criteria.append(clean_line)

    return criteria

def get_honua_architecture_rules() -> str:
    """Extract architecture rules from project documentation"""
    rules = """
# Honua Architecture Rules

## Critical Violations (Blocking):
1. **PR Process Violations**:
   - PR not linked to any GitHub issue
   - Missing acceptance criteria in linked issue
   - Changes don't address acceptance criteria

2. **Dependency Direction Violations**:
   - Core MUST NOT depend on Infrastructure (Honua.Postgres, Honua.Server)
   - Postgres MUST NOT depend on Server layer

3. **API Pattern Violations**:
   - NO Controllers - only Minimal APIs allowed
   - NO inheritance-heavy patterns

4. **Quality Gate Violations**:
   - Public types without XML documentation
   - Database types that are public (should be internal)

## Warning-Level Concerns:
1. **Organizational Issues**:
   - Layer-based instead of vertical slice organization
   - Violation of composition over inheritance

2. **Performance Issues**:
   - Synchronous operations in async context
   - Inefficient query patterns

3. **Dependency Complexity**:
   - >5 dependencies per endpoint
   - >4 dependencies per handler

## Good Patterns to Reinforce:
1. **Vertical Slices**: Features organized together, not by technical layer
2. **Clean Dependencies**: Core -> Infrastructure direction only
3. **Minimal APIs**: Simple, focused endpoints
4. **Clean Code**: Single responsibility, proper encapsulation
5. **Documentation**: All public APIs documented
"""
    return rules

def analyze_with_llm(context: str, api_key: Optional[str] = None, provider: str = "mock") -> str:
    """Analyze code using LLM API"""

    if provider == "mock" or not api_key:
        return mock_analysis(context)

    # TODO: Implement actual LLM providers
    if provider == "openai":
        return analyze_with_openai(context, api_key)
    elif provider == "anthropic":
        return analyze_with_anthropic(context, api_key)
    else:
        return mock_analysis(context)

def mock_analysis(context: str) -> str:
    """Generate mock analysis for testing"""
    # Check if context indicates missing issue link
    has_blocking_issue = "🚫 BLOCKING" in context

    if has_blocking_issue:
        return """**🏗️ Architecture Review Summary**

**🚫 Process Violations Found:**
- **BLOCKING**: PR not properly linked to GitHub issue
- Missing acceptance criteria validation

**✅ Good Patterns Found:**
- Following established project structure and naming conventions
- Proper separation of test concerns
- Clean project organization

**💡 Recommendations:**
- Link this PR to a GitHub issue using "Closes #123" or "Fixes #456"
- Ensure the linked issue has clear acceptance criteria
- Verify that changes actually address the acceptance criteria

**📚 Educational Notes:**
- Issue linking ensures traceability and proper change management
- Acceptance criteria provide clear success criteria for changes
- This process helps maintain project quality and team alignment

**Overall Assessment:** BLOCKING_ISSUES

*Note: This is a mock review. Configure OPENAI_API_KEY or ANTHROPIC_API_KEY for full LLM analysis.*"""
    else:
        return """**🏗️ Architecture Review Summary**

**✅ Good Patterns Found:**
- Following established project structure and naming conventions
- Proper separation of test concerns
- Clean project organization
- Proper GitHub issue linkage and acceptance criteria

**⚠️ Architecture Concerns:**
- No significant architectural violations detected in this changeset
- Changes appear to follow established patterns

**💡 Recommendations:**
- Continue following the established vertical slice organization
- Consider adding integration tests for any new public APIs
- Ensure new dependencies follow the Core -> Infrastructure direction

**📚 Educational Notes:**
- The changes maintain good separation of concerns
- Test organization follows project standards
- Architecture test infrastructure provides good foundation
- Proper issue tracking maintains project traceability

**Overall Assessment:** APPROVED

*Note: This is a mock review. Configure OPENAI_API_KEY or ANTHROPIC_API_KEY for full LLM analysis.*"""

def analyze_with_openai(context: str, api_key: str) -> str:
    """Analyze using OpenAI API"""
    try:
        import openai
        from openai import OpenAI
    except ImportError:
        return "Error: openai package not installed. Run: pip install openai"

    try:
        client = OpenAI(api_key=api_key)

        system_prompt = f"""You are a senior software architect reviewing code for the Honua geospatial feature server - a greenfield .NET 10 project implementing OGC standards with native AOT compilation.

## PROJECT CONTEXT:
- **Technology Stack**: .NET 10, Native AOT, Minimal APIs, PostgreSQL/PostGIS
- **Architecture**: Clean Architecture with Vertical Slices
- **Standards**: OGC API Features, GeoServices REST, OData v4, MVT
- **Quality Gate**: 80% line coverage, warnings-as-errors, sub-100ms cold start
- **Team Stage**: Establishing patterns for long-term maintainability

{get_honua_architecture_rules()}

## YOUR REVIEW FOCUS:

**🎯 Primary Concerns (BLOCKING):**
1. **Dependency Violations**: Core depending on Infrastructure
2. **API Pattern Violations**: Controllers instead of Minimal APIs
3. **Encapsulation Violations**: Public database/repository types

**⚠️ Secondary Concerns (NEEDS ATTENTION):**
1. **Organizational Anti-patterns**: Layer-based instead of vertical slices
2. **Complexity Issues**: Too many dependencies, inheritance hierarchies
3. **Performance Issues**: Sync over async, inefficient patterns

**✅ Positive Pattern Recognition:**
1. **Good Architecture**: Proper dependency flow, vertical organization
2. **Clean Design**: Single responsibility, composition over inheritance
3. **Quality Code**: Comprehensive documentation, proper encapsulation

## REVIEW STYLE:
- **Educational**: Explain WHY patterns matter for geospatial/performance/maintainability
- **Specific**: Reference exact files and line numbers
- **Actionable**: Provide concrete code examples and alternatives
- **Balanced**: Acknowledge good patterns, not just problems
- **Context-Aware**: Consider this is establishing patterns for the entire project"""

        user_prompt = f"""Analyze these code changes for architectural compliance using Honua's specific criteria:

## ASSESSMENT CRITERIA:

**🚫 BLOCKING_ISSUES** (Must fix before merge):
- PR not linked to any GitHub issue
- Missing acceptance criteria in linked issue
- Changes don't address acceptance criteria
- Core depending on Infrastructure (`using Honua.Postgres` in Core)
- Controller usage (`ControllerBase` inheritance)
- Public repository/database types (security violation)
- Missing XML docs on public APIs

**⚠️ NEEDS_ATTENTION** (Review recommended):
- Layer organization vs vertical slices
- >5 dependencies per endpoint, >4 per handler
- Deep inheritance (>3 levels)
- Sync over async patterns
- Complex methods (>10 parameters)

**✅ APPROVED** (Good patterns):
- Proper dependency flow
- Vertical slice organization
- Clean design patterns
- Comprehensive documentation

## CODE TO REVIEW:
{context}

Provide your review using this EXACT format:

**🏗️ Architecture Review Summary**

**✅ Good Patterns Found:**
- [List positive architectural decisions with file:line references]

**⚠️ Architecture Concerns:**
- [List potential violations with specific file:line references and severity level]

**💡 Recommendations:**
- [Specific actionable improvements with code examples]

**📚 Educational Notes:**
- [Explain WHY patterns matter for geospatial/AOT/performance context]

**Overall Assessment:** [APPROVED/NEEDS_ATTENTION/BLOCKING_ISSUES]

CRITICAL: First check PR/Issue process compliance:
1. Is this PR linked to a GitHub issue?
2. Does the linked issue have acceptance criteria?
3. Do the code changes actually address those acceptance criteria?

If any of these fail, mark as BLOCKING_ISSUES regardless of code quality."""

        response = client.chat.completions.create(
            model="gpt-4-turbo",  # Using GPT-4 Turbo for larger context window (128K tokens)
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt}
            ],
            max_tokens=2000,
            temperature=0.3  # Lower temperature for more consistent, focused analysis
        )

        analysis_result = response.choices[0].message.content

        # Extract assessment level for workflow
        assessment = extract_assessment_level(analysis_result)

        return analysis_result

    except Exception as e:
        return f"""**🏗️ Architecture Review Summary**

**⚠️ OpenAI Analysis Error:**
Error calling OpenAI API: {str(e)}

**💡 Fallback Recommendation:**
Please configure OPENAI_API_KEY in repository secrets and ensure OpenAI credits are available.

**Overall Assessment:** NEEDS_ATTENTION (API Configuration Issue)

*Falling back to basic static analysis...*"""

def extract_assessment_level(analysis_text: str) -> str:
    """Extract the overall assessment level from analysis"""
    import re

    # Look for "Overall Assessment:" line
    match = re.search(r'Overall Assessment:\s*([A-Z_]+)', analysis_text)
    if match:
        return match.group(1)

    # Fallback: check for keywords
    analysis_lower = analysis_text.lower()

    blocking_keywords = [
        "no github issue", "not linked to.*issue", "missing issue",
        "no acceptance criteria", "missing.*criteria", "changes.*don't.*address",
        "dependency violation", "controller inheritance", "controllerbase",
        "public repository", "public.*repository", "public.*dataaccess",
        "missing xml documentation", "infrastructure dependency"
    ]

    warning_keywords = [
        "layer organization", "too many dependencies", "inheritance hierarchy",
        "sync over async", "god class", "complex method", "needs attention"
    ]

    if any(re.search(keyword, analysis_lower) for keyword in blocking_keywords):
        return "BLOCKING_ISSUES"

    if any(re.search(keyword, analysis_lower) for keyword in warning_keywords):
        return "NEEDS_ATTENTION"

    return "APPROVED"

def analyze_with_anthropic(context: str, api_key: str) -> str:
    """Analyze using Anthropic Claude API (placeholder)"""
    # TODO: Implement Anthropic integration
    prompt = f"""You are an expert software architect reviewing code for the Honua geospatial server.

{get_honua_architecture_rules()}

Analyze these changes and provide feedback in the specified format:

{context}

Provide architectural review focusing on the rules above."""

    # Implementation would go here
    return "Anthropic analysis not yet implemented"

def get_changed_files(base_ref: str, head_ref: str) -> List[str]:
    """Get list of changed C# files"""
    try:
        result = subprocess.run([
            'git', 'diff', '--name-only', f"{base_ref}...{head_ref}"
        ], capture_output=True, text=True, check=True)

        files = [f for f in result.stdout.strip().split('\n')
                if f.endswith('.cs') or f.endswith('.csproj')]
        return [f for f in files if f]  # Filter empty strings
    except subprocess.CalledProcessError as e:
        print(f"Error getting changed files: {e}")
        return []

def get_file_content_and_diff(file_path: str, base_ref: str, head_ref: str) -> Dict[str, str]:
    """Get file content and diff for analysis

    For .cs files: Reads complete file content for full architectural context
    For other files: Limits to 2000 chars to prevent token bloat
    """
    content = ""
    diff = ""

    try:
        if os.path.exists(file_path):
            with open(file_path, 'r', encoding='utf-8') as f:
                file_content = f.read()

                # For .cs files, include complete content for architectural analysis
                if file_path.endswith('.cs'):
                    content = file_content
                else:
                    # For non-C# files, limit to prevent token bloat
                    content = file_content[:2000] + ("...[truncated]" if len(file_content) > 2000 else "")

        # Get diff - always full diff for architectural context
        result = subprocess.run([
            'git', 'diff', f"{base_ref}...{head_ref}", '--', file_path
        ], capture_output=True, text=True, check=True)
        diff = result.stdout

    except Exception as e:
        print(f"Error processing {file_path}: {e}")

    return {"content": content, "diff": diff}

def main():
    """Main analysis function"""
    # Get environment variables
    base_ref = os.environ.get('GITHUB_BASE_REF', 'main')
    head_ref = os.environ.get('GITHUB_SHA', 'HEAD')
    api_key = os.environ.get('OPENAI_API_KEY') or os.environ.get('ANTHROPIC_API_KEY')

    # Determine provider
    provider = "mock"
    if os.environ.get('OPENAI_API_KEY'):
        provider = "openai"
    elif os.environ.get('ANTHROPIC_API_KEY'):
        provider = "anthropic"

    print(f"Using provider: {provider}")
    print(f"Analyzing changes from {base_ref} to {head_ref}")

    # Get changed files
    changed_files = get_changed_files(f"origin/{base_ref}", head_ref)

    if not changed_files:
        print("No C# files changed, skipping analysis")
        sys.exit(0)

    print(f"Found {len(changed_files)} changed files")

    # Get PR and issue information
    pr_info = get_pr_info()
    print(f"PR Information: {pr_info.get('title', 'Unknown')}")

    # Build context
    context = f"""
# Architecture Review Context

## PR Information:
- **Title**: {pr_info.get('title', 'Unknown')}
- **Number**: {pr_info.get('number', 'Unknown')}
- **Linked Issues**: {len(pr_info.get('linked_issues', []))} issue(s)

"""

    # Add issue information
    if 'error' in pr_info:
        context += f"**⚠️ PR Analysis Error**: {pr_info['error']}\n\n"
    elif not pr_info.get('linked_issues'):
        context += "**🚫 BLOCKING**: No GitHub issues linked to this PR\n\n"
    else:
        context += "## Linked Issues:\n"
        for issue in pr_info['linked_issues']:
            if 'error' in issue:
                context += f"- Issue #{issue.get('number', '?')}: Error - {issue['error']}\n"
            else:
                context += f"- **Issue #{issue['number']}**: {issue['title']}\n"
                ac_count = len(issue.get('acceptance_criteria', []))
                if ac_count == 0:
                    context += f"  **🚫 BLOCKING**: No acceptance criteria found\n"
                else:
                    context += f"  **Acceptance Criteria** ({ac_count} items):\n"
                    for i, criteria in enumerate(issue['acceptance_criteria'][:5], 1):  # Limit to prevent bloat
                        context += f"    {i}. {criteria}\n"
                    if ac_count > 5:
                        context += f"    ... and {ac_count - 5} more\n"
        context += "\n"

    context += f"""
## Changed Files: {len(changed_files)}
{chr(10).join(f"- {f}" for f in changed_files)}

## File Analysis:
"""

    for file_path in changed_files[:10]:  # Limit to prevent context overflow
        file_info = get_file_content_and_diff(file_path, f"origin/{base_ref}", head_ref)

        # Use appropriate language identifier for syntax highlighting
        language = "csharp" if file_path.endswith('.cs') else "text"

        context += f"""
### File: {file_path}

#### Current Content:
```{language}
{file_info['content']}
```

#### Changes:
```diff
{file_info['diff']}
```
"""

    # Perform analysis
    analysis = analyze_with_llm(context, api_key, provider)

    # Extract assessment level
    assessment = extract_assessment_level(analysis)

    # Output result for GitHub Actions
    output_file = os.environ.get('GITHUB_OUTPUT')
    if output_file:
        with open(output_file, 'a') as f:
            f.write(f"analysis<<EOF\n{analysis}\nEOF\n")
            f.write(f"assessment={assessment}\n")

    print(f"Analysis complete - Assessment: {assessment}")
    print("=" * 50)
    print(analysis)

    # Set exit code based on assessment (for optional strict mode)
    if os.environ.get('STRICT_MODE') == 'true' and assessment == 'BLOCKING_ISSUES':
        print("STRICT MODE: Exiting with error due to blocking issues")
        sys.exit(1)

if __name__ == "__main__":
    main()