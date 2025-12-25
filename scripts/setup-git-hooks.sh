#!/usr/bin/env bash
# Setup script to install git hooks for pre-PR validation enforcement

set -e

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}🔧 Setting up git hooks for pre-PR validation...${NC}"
echo

# Check if we're in a git repository
if [ ! -d ".git" ]; then
    echo "❌ ERROR: Not in a git repository. Please run from project root."
    exit 1
fi

# Check if we're in the right directory
if [ ! -f "Honua.sln" ]; then
    echo "❌ ERROR: Please run this script from the project root directory"
    exit 1
fi

echo -e "${YELLOW}📋 This will install git hooks that:${NC}"
echo "   • Run pre-PR validation before every push"
echo "   • Prevent pushes that would fail CI"
echo "   • Enforce code quality standards"
echo
read -p "Continue? (y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Setup cancelled."
    exit 0
fi

# Create hooks directory if it doesn't exist
mkdir -p .git/hooks

# Install pre-push hook
echo -e "${YELLOW}📦 Installing pre-push hook...${NC}"
cp scripts/hooks/pre-push .git/hooks/pre-push
chmod +x .git/hooks/pre-push

# Make the pre-PR script executable
echo -e "${YELLOW}🔧 Making pre-PR script executable...${NC}"
chmod +x scripts/pre-pr-check.sh

# Test if the hook works
echo -e "${YELLOW}🧪 Testing hook installation...${NC}"
if [ -f ".git/hooks/pre-push" ] && [ -x ".git/hooks/pre-push" ]; then
    echo -e "${GREEN}✅ Pre-push hook installed successfully!${NC}"
else
    echo "❌ Hook installation failed!"
    exit 1
fi

echo
echo -e "${GREEN}🎉 Git hooks setup complete!${NC}"
echo
echo -e "${BLUE}ℹ️  What happens now:${NC}"
echo "   • Every 'git push' will run pre-PR validation"
echo "   • Failed validation will prevent the push"
echo "   • This catches issues before CI runs"
echo
echo -e "${BLUE}💡 Tips:${NC}"
echo "   • Run 'scripts/pre-pr-check.sh' manually anytime"
echo "   • Use 'git push --no-verify' to bypass (not recommended)"
echo "   • Hooks only affect this local repository"
echo
echo -e "${BLUE}📚 Next steps for the team:${NC}"
echo "   • Each developer should run: scripts/setup-git-hooks.sh"
echo "   • Add to onboarding documentation"
echo "   • Consider adding to README setup instructions"
echo