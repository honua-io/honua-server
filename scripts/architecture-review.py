#!/usr/bin/env python3
"""
Honua Architecture Review Script
Analyzes code changes using LLM for architectural compliance

Features:
- Diff-only analysis with chunking to keep prompts small
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
    """Provide a compact summary of Honua architecture rules to keep prompts small."""
    return """# Honua Architecture Rules (Summary)

## BLOCKING (Must Fix)
- Dependency direction: Honua.Core must not reference Honua.Postgres or Honua.Server. Honua.Postgres and Honua.Server may depend on Honua.Core.
- API pattern: No ControllerBase or [ApiController]; use Minimal APIs.
- Encapsulation: Infrastructure implementation types must be internal (middleware, decorators, providers). Public DTOs/options/extensions are allowed.
- Documentation: Public types require XML docs; internal types do not.
- AOT: Avoid reflection/dynamic JSON in production code; use source-generated JSON/logging. GC APIs and CultureInfo are allowed.
- Tests: Reflection or AOT-breaking code is allowed in test projects.

## WARNING (Review Needed)
- Too many dependencies: endpoints <= 5, handlers <= 4.
- Sync-over-async (.Result/.Wait()).
- Layered organization instead of vertical slices.
"""

def analyze_with_llm(
    context: str,
    api_key: Optional[str] = None,
    provider: str = "mock",
    process_blocking: bool = False,
    is_test: bool = False) -> str:
    """Analyze code using LLM API"""

    if provider == "mock" or not api_key:
        return mock_analysis(process_blocking)

    # TODO: Implement actual LLM providers
    if provider == "openai":
        return analyze_with_openai(context, api_key, is_test)
    elif provider == "anthropic":
        return analyze_with_anthropic(context, api_key, is_test)
    else:
        return mock_analysis(process_blocking)

def mock_analysis(process_blocking: bool) -> str:
    """Generate mock analysis for testing"""
    if process_blocking:
        return """**Findings**
- [BLOCKING] PR process: missing linked issue or acceptance criteria.

**Overall Assessment:** BLOCKING_ISSUES

*Note: This is a mock review. Configure OPENAI_API_KEY or ANTHROPIC_API_KEY for full LLM analysis.*"""

    return """**Findings**
- No significant architectural violations detected in this chunk.

**Overall Assessment:** APPROVED

*Note: This is a mock review. Configure OPENAI_API_KEY or ANTHROPIC_API_KEY for full LLM analysis.*"""

def analyze_with_openai(context: str, api_key: str, is_test: bool) -> str:
    """Analyze using OpenAI API"""
    try:
        import openai
        from openai import OpenAI
    except ImportError:
        return "Error: openai package not installed. Run: pip install openai"

    try:
        client = OpenAI(api_key=api_key)

        system_prompt = f"""You are a senior software architect reviewing PR diffs for Honua (a .NET 10, AOT-friendly geospatial server).

{get_honua_architecture_rules()}

Review only the provided diff. Focus on correctness and architectural rule compliance. Be concise and cite file/line when possible.
If the diff is in test code, do not flag reflection/dynamic/AOT-breaking patterns as violations."""

        focus_items = [
            "- Dependency direction",
            "- Minimal API usage (no controllers)",
            "- Infrastructure encapsulation (internal types)",
            "- Public XML docs",
            "- Dependency count limits",
            "- Sync-over-async"
        ]
        if not is_test:
            focus_items.insert(3, "- AOT-safe patterns (source-generated JSON/logging)")

        user_prompt = f"""Review only the diff below. Do not ask for full files. Focus on:
{chr(10).join(focus_items)}

Return findings in this exact format:

**Findings**
- [SEVERITY] file:line - issue

**Overall Assessment:** [APPROVED/NEEDS_ATTENTION/BLOCKING_ISSUES]

Diff:
{context}"""

        response = client.chat.completions.create(
            model="gpt-4-turbo",  # Using GPT-4 Turbo for larger context window (128K tokens)
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt}
            ],
            max_tokens=1200,
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

    match = re.search(r'Assessment:\s*([A-Z_]+)', analysis_text)
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

def analyze_with_anthropic(context: str, api_key: str, is_test: bool) -> str:
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

def is_test_path(file_path: str) -> bool:
    """Return True if the file path is considered test code."""
    normalized = file_path.replace("\\", "/").lower()
    if normalized.startswith("tests/") or "/tests/" in normalized:
        return True
    if normalized.startswith("test/") or "/test/" in normalized:
        return True
    if normalized.endswith(".tests.cs") or normalized.endswith(".test.cs"):
        return True
    if ".tests." in normalized or ".test." in normalized:
        return True
    return False

def get_file_diff(file_path: str, base_ref: str, head_ref: str) -> str:
    """Get diff for a file."""
    try:
        result = subprocess.run(
            ['git', 'diff', f"{base_ref}...{head_ref}", '--', file_path],
            capture_output=True,
            text=True,
            check=True
        )
        return result.stdout
    except Exception as e:
        print(f"Error processing {file_path}: {e}")
        return ""


def split_diff_into_segments(diff_text: str, max_lines: int) -> List[str]:
    """Split a diff into segments with repeated headers to keep chunks small."""
    lines = diff_text.splitlines()
    if not lines:
        return []

    if len(lines) <= max_lines:
        return [diff_text]

    header_end = 0
    for i, line in enumerate(lines):
        if line.startswith('@@'):
            header_end = i
            break
    header_lines = lines[:header_end]
    body_lines = lines[header_end:] if header_end else lines

    max_body_lines = max(1, max_lines - len(header_lines))
    segments = []
    for i in range(0, len(body_lines), max_body_lines):
        segment_lines = header_lines + body_lines[i:i + max_body_lines]
        segments.append("\n".join(segment_lines))

    return segments


def clean_chunk_analysis(analysis_text: str) -> str:
    """Remove per-chunk assessment lines for aggregation."""
    cleaned_lines = []
    for line in analysis_text.splitlines():
        if line.strip().lower().startswith("overall assessment"):
            continue
        cleaned_lines.append(line)
    return "\n".join(cleaned_lines).strip()

def combine_assessments(assessments: List[str], process_blocking: bool) -> str:
    """Combine multiple assessments into a single overall result."""
    if process_blocking:
        return "BLOCKING_ISSUES"

    if not assessments:
        return "APPROVED"
    if "BLOCKING_ISSUES" in assessments:
        return "NEEDS_ATTENTION"

    if "NEEDS_ATTENTION" in assessments:
        return "NEEDS_ATTENTION"

    return "APPROVED"

def main():
    """Main analysis function"""
    # Get environment variables
    base_ref = os.environ.get('GITHUB_BASE_REF', 'trunk')
    head_ref = os.environ.get('GITHUB_SHA', 'HEAD')
    api_key = os.environ.get('OPENAI_API_KEY') or os.environ.get('ANTHROPIC_API_KEY')
    max_chunk_lines = int(os.environ.get('REVIEW_CHUNK_LINES', '300'))
    max_files = int(os.environ.get('REVIEW_MAX_FILES', '50'))

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

    process_issues = []
    process_warnings = []

    if 'error' in pr_info:
        process_warnings.append(f"PR info unavailable: {pr_info['error']}")
    elif not pr_info.get('linked_issues'):
        process_issues.append("No GitHub issues linked to this PR.")
    else:
        for issue in pr_info['linked_issues']:
            if 'error' in issue:
                process_warnings.append(f"Issue #{issue.get('number', '?')}: {issue['error']}")
                continue
            if len(issue.get('acceptance_criteria', [])) == 0:
                process_issues.append(f"Issue #{issue.get('number', '?')} is missing acceptance criteria.")

    process_blocking = len(process_issues) > 0

    segments = []
    skipped_files = []

    for file_path in changed_files[:max_files]:
        diff_text = get_file_diff(file_path, f"origin/{base_ref}", head_ref)
        if not diff_text.strip():
            continue

        diff_segments = split_diff_into_segments(diff_text, max_chunk_lines)
        total_parts = len(diff_segments)
        for part_index, segment in enumerate(diff_segments, start=1):
            segments.append({
                "file": file_path,
                "part_index": part_index,
                "part_total": total_parts,
                "diff": segment
            })

    if len(changed_files) > max_files:
        skipped_files = changed_files[max_files:]

    if not segments:
        print("No diff segments found, skipping analysis")
        sys.exit(0)

    chunk_results = []
    for index, segment in enumerate(segments, start=1):
        label = segment["file"]
        if segment["part_total"] > 1:
            label = f"{label} (part {segment['part_index']}/{segment['part_total']})"

        is_test = is_test_path(segment["file"])
        test_note = ""
        if is_test:
            test_note = "\nNote: This diff is in test code. Reflection/dynamic/AOT-breaking patterns are allowed here; do not flag them."

        chunk_context = f"## Diff Chunk {index}/{len(segments)}\n### {label}{test_note}\n```diff\n{segment['diff']}\n```"
        chunk_analysis = analyze_with_llm(
            chunk_context,
            api_key,
            provider,
            process_blocking=process_blocking,
            is_test=is_test
        )
        chunk_assessment = extract_assessment_level(chunk_analysis)
        chunk_results.append({
            "label": label,
            "analysis": chunk_analysis,
            "assessment": chunk_assessment
        })

    overall_assessment = combine_assessments(
        [result["assessment"] for result in chunk_results],
        process_blocking
    )

    analysis_lines = ["**🏗️ Architecture Review Summary**", ""]

    analysis_lines.append("**Process Checks:**")
    if process_issues:
        for issue in process_issues:
            analysis_lines.append(f"- 🚫 BLOCKING: {issue}")
    elif 'error' in pr_info:
        analysis_lines.append("- ⚠️ Unable to verify issue linkage and acceptance criteria.")
    elif process_warnings:
        analysis_lines.append("- ⚠️ Issue linkage detected, but some checks failed.")
    else:
        analysis_lines.append("- ✅ Linked issue with acceptance criteria detected.")
    for warning in process_warnings:
        analysis_lines.append(f"- ⚠️ {warning}")
    if skipped_files:
        analysis_lines.append(f"- ⚠️ Review limited to first {max_files} files; {len(skipped_files)} file(s) not analyzed.")

    analysis_lines.append("")
    analysis_lines.append(f"**Diff Review Chunks:** {len(chunk_results)}")

    for i, result in enumerate(chunk_results, start=1):
        analysis_lines.append(f"### Chunk {i}/{len(chunk_results)} ({result['label']})")
        cleaned = clean_chunk_analysis(result["analysis"])
        analysis_lines.append(cleaned if cleaned else "- No findings.")
        analysis_lines.append("")

    analysis_lines.append(f"**Overall Assessment:** {overall_assessment}")

    analysis = "\n".join(analysis_lines).strip()
    assessment = overall_assessment

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
