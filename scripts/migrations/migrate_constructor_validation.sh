#!/bin/bash

# Constructor Validation Migration Script (Bash)
# Automatically refactors constructor null validation patterns to use the new validation framework

set -euo pipefail

# Default values
PROJECT_PATH="."
DRY_RUN=false
VERBOSE=false

# Color codes
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
WHITE='\033[1;37m'
GRAY='\033[0;37m'
NC='\033[0m' # No Color

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --project-path)
            PROJECT_PATH="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --help)
            echo "Constructor Validation Migration Script"
            echo ""
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --project-path PATH    Path to search for C# files (default: .)"
            echo "  --dry-run             Show what would be changed without modifying files"
            echo "  --verbose             Show detailed output"
            echo "  --help                Show this help message"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

echo -e "${GREEN}🔧 Constructor Validation Migration Script${NC}"
echo -e "${GREEN}============================================${NC}"

if $DRY_RUN; then
    echo -e "${YELLOW}🔍 DRY RUN MODE - No files will be modified${NC}"
fi

# Function to check if file needs validation using statement
needs_validation_using() {
    local file_content="$1"
    if echo "$file_content" | grep -q "using Honua.Core.Features.Infrastructure.Validation"; then
        return 1  # false - already has using
    else
        return 0  # true - needs using
    fi
}

# Function to add validation using statement
add_validation_using() {
    local file_content="$1"

    # Find the last using statement and add after it
    local last_using_line=$(echo "$file_content" | grep -n "^using " | tail -1 | cut -d: -f1)

    if [ -n "$last_using_line" ]; then
        # Insert the new using statement after the last using
        echo "$file_content" | sed "${last_using_line}a\\using Honua.Core.Features.Infrastructure.Validation;"
    else
        echo "$file_content"
    fi
}

# Function to apply basic null check pattern
apply_basic_null_check() {
    local content="$1"
    echo "$content" | sed -E 's/([[:alnum:]_]+)[[:space:]]*=[[:space:]]*([[:alnum:]_]+)[[:space:]]*\?\?[[:space:]]*throw[[:space:]]+new[[:space:]]+ArgumentNullException\(nameof\([[:alnum:]_]+\)\);/\1 = \2.ThrowIfNull();/g'
}

# Function to apply options pattern
apply_options_pattern() {
    local content="$1"
    echo "$content" | sed -E 's/([[:alnum:]_]+)[[:space:]]*=[[:space:]]*([[:alnum:]_]+)\?\.Value[[:space:]]*\?\?[[:space:]]*throw[[:space:]]+new[[:space:]]+ArgumentNullException\(nameof\([[:alnum:]_]+\)\);/\1 = \2.ValidateAndGetValue();/g'
}

# Function to check if content contains validation patterns
contains_validation_pattern() {
    local content="$1"

    # Check for basic null check pattern
    if echo "$content" | grep -qE "[[:alnum:]_]+[[:space:]]*=[[:space:]]*[[:alnum:]_]+[[:space:]]*\?\?[[:space:]]*throw[[:space:]]+new[[:space:]]+ArgumentNullException\(nameof\([[:alnum:]_]+\)\);"; then
        return 0
    fi

    # Check for options pattern
    if echo "$content" | grep -qE "[[:alnum:]_]+[[:space:]]*=[[:space:]]*[[:alnum:]_]+\?\.Value[[:space:]]*\?\?[[:space:]]*throw[[:space:]]+new[[:space:]]+ArgumentNullException\(nameof\([[:alnum:]_]+\)\);"; then
        return 0
    fi

    return 1
}

# Function to process a single file
process_file() {
    local file_path="$1"
    local file_name=$(basename "$file_path")

    echo -e "${WHITE}🔄 Processing: $file_name${NC}"

    if [ ! -f "$file_path" ]; then
        echo -e "  ${RED}❌ File not found: $file_path${NC}"
        return 1
    fi

    local original_content=$(cat "$file_path")
    local modified_content="$original_content"
    local patterns_applied=0

    # Apply basic null check pattern
    local new_content=$(apply_basic_null_check "$modified_content")
    if [ "$new_content" != "$modified_content" ]; then
        modified_content="$new_content"
        patterns_applied=$((patterns_applied + 1))
        echo -e "  ${BLUE}📝 Applied pattern: BasicNullCheck${NC}"
    fi

    # Apply options pattern
    new_content=$(apply_options_pattern "$modified_content")
    if [ "$new_content" != "$modified_content" ]; then
        modified_content="$new_content"
        patterns_applied=$((patterns_applied + 1))
        echo -e "  ${BLUE}📝 Applied pattern: OptionsPattern${NC}"
    fi

    # Add using statement if needed
    if [ $patterns_applied -gt 0 ] && needs_validation_using "$modified_content"; then
        echo -e "  ${BLUE}📦 Adding validation using statement${NC}"
        modified_content=$(add_validation_using "$modified_content")
    fi

    if [ $patterns_applied -gt 0 ]; then
        echo -e "  ${GREEN}✅ Modified - Applied $patterns_applied patterns${NC}"

        if ! $DRY_RUN; then
            echo "$modified_content" > "$file_path"
            echo -e "  ${GREEN}💾 File saved${NC}"
        fi

        return 0  # File was modified
    else
        echo -e "  ${YELLOW}⏭️ No changes needed${NC}"
        return 1  # File was not modified
    fi
}

# Main migration logic
echo -e "${CYAN}🔍 Scanning for C# files...${NC}"

# Find all C# files, excluding build directories
declare -a csharp_files
while IFS= read -r -d '' file; do
    csharp_files+=("$file")
done < <(find "$PROJECT_PATH" -name "*.cs" -type f ! -path "*/bin/*" ! -path "*/obj/*" ! -path "*/.git/*" -print0)

# Filter files that contain validation patterns
declare -a files_to_process
for file in "${csharp_files[@]}"; do
    if [ -f "$file" ]; then
        content=$(cat "$file")
        if contains_validation_pattern "$content"; then
            files_to_process+=("$file")
        fi
    fi
done

echo -e "${CYAN}📊 Found ${#csharp_files[@]} C# files, ${#files_to_process[@]} contain validation patterns${NC}"

total_files=0
modified_files=0

for file in "${files_to_process[@]}"; do
    total_files=$((total_files + 1))

    if process_file "$file"; then
        modified_files=$((modified_files + 1))
    fi

    if $VERBOSE; then
        echo -e "  ${GRAY}🔍 File path: $file${NC}"
    fi
done

echo ""
echo -e "${GREEN}📈 Migration Summary${NC}"
echo -e "${GREEN}===================${NC}"
echo -e "${WHITE}Files processed: $total_files${NC}"
echo -e "${GREEN}Files modified: $modified_files${NC}"

if $DRY_RUN; then
    echo ""
    echo -e "${YELLOW}🚀 To apply changes, run without --dry-run flag${NC}"
    echo -e "${GRAY}Example: ./migrate_constructor_validation.sh --project-path src/${NC}"
else
    echo ""
    echo -e "${GREEN}✅ Migration completed!${NC}"
    echo -e "${YELLOW}🧪 Run tests to verify no behavioral changes${NC}"
fi

echo ""
echo -e "${CYAN}📋 Next Steps:${NC}"
echo -e "${WHITE}1. Review modified files for correctness${NC}"
echo -e "${WHITE}2. Run full test suite: dotnet test${NC}"
echo -e "${WHITE}3. Check build: dotnet build${NC}"
echo -e "${WHITE}4. Code review changes before commit${NC}"