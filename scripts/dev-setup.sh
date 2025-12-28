#!/bin/bash
set -euo pipefail

# Development Environment Setup Script for Honua Server
# Sets up a complete development environment with all dependencies

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
OS_TYPE="$(uname -s)"
ARCH_TYPE="$(uname -m)"

echo "🚀 Honua Server Development Environment Setup"
echo "=============================================="
echo "OS: $OS_TYPE"
echo "Architecture: $ARCH_TYPE"
echo "Project Root: $PROJECT_ROOT"

# Function to check if command exists
command_exists() {
    command -v "$1" >/dev/null 2>&1
}

# Function to detect package manager
detect_package_manager() {
    if command_exists apt-get; then
        echo "apt"
    elif command_exists yum; then
        echo "yum"
    elif command_exists dnf; then
        echo "dnf"
    elif command_exists brew; then
        echo "brew"
    elif command_exists pacman; then
        echo "pacman"
    else
        echo "unknown"
    fi
}

# Function to install system packages
install_system_packages() {
    local package_manager=$(detect_package_manager)

    echo "📦 Installing system dependencies using $package_manager..."

    case "$package_manager" in
        "apt")
            sudo apt-get update
            sudo apt-get install -y \
                curl \
                wget \
                git \
                unzip \
                build-essential \
                ca-certificates \
                gnupg \
                lsb-release \
                jq \
                postgresql-client \
                python3 \
                python3-pip
            ;;
        "yum"|"dnf")
            local pm_cmd=$package_manager
            sudo $pm_cmd update -y
            sudo $pm_cmd install -y \
                curl \
                wget \
                git \
                unzip \
                gcc \
                gcc-c++ \
                make \
                ca-certificates \
                gnupg \
                jq \
                postgresql \
                python3 \
                python3-pip
            ;;
        "brew")
            brew update
            brew install \
                curl \
                wget \
                git \
                jq \
                postgresql \
                python3
            ;;
        "pacman")
            sudo pacman -Syu
            sudo pacman -S --noconfirm \
                curl \
                wget \
                git \
                unzip \
                base-devel \
                ca-certificates \
                gnupg \
                jq \
                postgresql \
                python
            ;;
        *)
            echo "⚠️  Unknown package manager. Please install dependencies manually:"
            echo "   - curl, wget, git, unzip, build tools, jq, postgresql-client, python3"
            ;;
    esac
}

# Function to install .NET SDK
install_dotnet() {
    echo "🔵 Installing .NET 10 SDK..."

    if command_exists dotnet; then
        local current_version=$(dotnet --version 2>/dev/null || echo "0.0.0")
        if [[ "$current_version" == "10."* ]]; then
            echo "✅ .NET 10 SDK already installed: $current_version"
            return 0
        fi
    fi

    # Install .NET using the official script
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0

    # Add to PATH
    export PATH="$HOME/.dotnet:$PATH"
    echo 'export PATH="$HOME/.dotnet:$PATH"' >> "$HOME/.bashrc"
    echo 'export PATH="$HOME/.dotnet:$PATH"' >> "$HOME/.profile"

    # Verify installation
    if "$HOME/.dotnet/dotnet" --version >/dev/null 2>&1; then
        echo "✅ .NET SDK installed successfully"
    else
        echo "❌ .NET SDK installation failed"
        exit 1
    fi
}

# Function to install Docker
install_docker() {
    echo "🐳 Installing Docker..."

    if command_exists docker; then
        echo "✅ Docker already installed: $(docker --version)"
        return 0
    fi

    case "$OS_TYPE" in
        "Linux")
            # Install Docker using official script
            curl -fsSL https://get.docker.com -o get-docker.sh
            sudo sh get-docker.sh
            rm get-docker.sh

            # Add user to docker group
            sudo usermod -aG docker "$USER"

            # Install Docker Compose
            local compose_version="v2.23.0"
            sudo curl -L "https://github.com/docker/compose/releases/download/$compose_version/docker-compose-$(uname -s)-$(uname -m)" \
                -o /usr/local/bin/docker-compose
            sudo chmod +x /usr/local/bin/docker-compose
            ;;
        "Darwin")
            echo "Please install Docker Desktop for Mac from https://docker.com/products/docker-desktop"
            echo "Press Enter when Docker Desktop is installed..."
            read
            ;;
        *)
            echo "❌ Unsupported OS for automatic Docker installation: $OS_TYPE"
            echo "Please install Docker manually"
            exit 1
            ;;
    esac

    echo "✅ Docker installed. You may need to log out and back in for group changes."
}

# Function to install kubectl
install_kubectl() {
    echo "☸️  Installing kubectl..."

    if command_exists kubectl; then
        echo "✅ kubectl already installed: $(kubectl version --client --short 2>/dev/null || echo 'installed')"
        return 0
    fi

    local kubectl_version="v1.28.4"
    local kubectl_url=""

    case "$OS_TYPE" in
        "Linux")
            case "$ARCH_TYPE" in
                "x86_64")
                    kubectl_url="https://dl.k8s.io/release/$kubectl_version/bin/linux/amd64/kubectl"
                    ;;
                "aarch64"|"arm64")
                    kubectl_url="https://dl.k8s.io/release/$kubectl_version/bin/linux/arm64/kubectl"
                    ;;
                *)
                    echo "❌ Unsupported architecture: $ARCH_TYPE"
                    return 1
                    ;;
            esac
            ;;
        "Darwin")
            case "$ARCH_TYPE" in
                "x86_64")
                    kubectl_url="https://dl.k8s.io/release/$kubectl_version/bin/darwin/amd64/kubectl"
                    ;;
                "arm64")
                    kubectl_url="https://dl.k8s.io/release/$kubectl_version/bin/darwin/arm64/kubectl"
                    ;;
                *)
                    echo "❌ Unsupported architecture: $ARCH_TYPE"
                    return 1
                    ;;
            esac
            ;;
        *)
            echo "❌ Unsupported OS: $OS_TYPE"
            return 1
            ;;
    esac

    # Download and install kubectl
    curl -LO "$kubectl_url"
    chmod +x kubectl
    sudo mv kubectl /usr/local/bin/

    echo "✅ kubectl installed successfully"
}

# Function to install development tools
install_dev_tools() {
    echo "🛠️  Installing development tools..."

    # Install Helm
    if ! command_exists helm; then
        echo "📊 Installing Helm..."
        curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
    else
        echo "✅ Helm already installed: $(helm version --short 2>/dev/null || echo 'installed')"
    fi

    # Install k9s (optional but useful)
    if ! command_exists k9s; then
        echo "🖥️  Installing k9s..."
        case "$OS_TYPE" in
            "Linux")
                curl -sS https://webinstall.dev/k9s | bash
                ;;
            "Darwin")
                if command_exists brew; then
                    brew install k9s
                else
                    echo "⚠️  Please install k9s manually or install Homebrew"
                fi
                ;;
        esac
    else
        echo "✅ k9s already installed"
    fi

    # Install Python dependencies for testing
    echo "🐍 Installing Python testing dependencies..."
    pip3 install --user \
        requests \
        pytest \
        pytest-asyncio \
        httpx
}

# Function to setup project environment
setup_project_env() {
    echo "📁 Setting up project environment..."

    cd "$PROJECT_ROOT"

    # Restore .NET packages
    if [ -f "Honua.sln" ]; then
        echo "📦 Restoring .NET packages..."
        dotnet restore Honua.sln
    fi

    # Setup git hooks
    if [ -f "scripts/setup-git-hooks.sh" ]; then
        echo "🪝 Setting up git hooks..."
        bash scripts/setup-git-hooks.sh
    fi

    # Create .env files for development
    if [ -f "scripts/secret-management.sh" ]; then
        echo "🔐 Generating development secrets..."
        ENV_TYPE=development SECRET_BACKEND=file bash scripts/secret-management.sh generate
    fi

    # Setup test database
    echo "🗄️  Setting up test database..."
    if command_exists docker; then
        # Start postgres for development
        docker-compose up -d postgres

        # Wait for postgres to be ready
        echo "⏳ Waiting for PostgreSQL to be ready..."
        timeout=60
        while ! docker-compose exec -T postgres pg_isready -U honua_user -d honua_dev >/dev/null 2>&1; do
            sleep 2
            timeout=$((timeout - 2))
            if [ $timeout -le 0 ]; then
                echo "❌ PostgreSQL did not start within 60 seconds"
                break
            fi
        done

        if [ $timeout -gt 0 ]; then
            echo "✅ PostgreSQL is ready"
        fi
    fi
}

# Function to validate installation
validate_installation() {
    echo "🔍 Validating installation..."

    local failed_checks=0

    # Check .NET
    if command_exists dotnet; then
        local dotnet_version=$(dotnet --version 2>/dev/null || echo "ERROR")
        if [[ "$dotnet_version" == "10."* ]]; then
            echo "✅ .NET SDK: $dotnet_version"
        else
            echo "❌ .NET SDK: $dotnet_version (expected 10.x)"
            ((failed_checks++))
        fi
    else
        echo "❌ .NET SDK: Not found"
        ((failed_checks++))
    fi

    # Check Docker
    if command_exists docker; then
        local docker_version=$(docker --version 2>/dev/null || echo "ERROR")
        echo "✅ Docker: $docker_version"
    else
        echo "❌ Docker: Not found"
        ((failed_checks++))
    fi

    # Check Docker Compose
    if command_exists docker-compose || docker compose version >/dev/null 2>&1; then
        local compose_version=$(docker-compose --version 2>/dev/null || docker compose version 2>/dev/null || echo "ERROR")
        echo "✅ Docker Compose: $compose_version"
    else
        echo "❌ Docker Compose: Not found"
        ((failed_checks++))
    fi

    # Check kubectl
    if command_exists kubectl; then
        local kubectl_version=$(kubectl version --client --short 2>/dev/null | head -1 || echo "installed")
        echo "✅ kubectl: $kubectl_version"
    else
        echo "⚠️  kubectl: Not found (optional)"
    fi

    # Check project build
    if [ -f "$PROJECT_ROOT/Honua.sln" ]; then
        echo "🔍 Testing project build..."
        cd "$PROJECT_ROOT"
        if dotnet build Honua.sln --configuration Debug >/dev/null 2>&1; then
            echo "✅ Project builds successfully"
        else
            echo "❌ Project build failed"
            ((failed_checks++))
        fi
    fi

    return $failed_checks
}

# Function to display next steps
show_next_steps() {
    echo ""
    echo "🎉 Development Environment Setup Complete!"
    echo "=========================================="
    echo ""
    echo "Next steps:"
    echo "1. Reload your shell or run: source ~/.bashrc"
    echo "2. Test the build: cd $PROJECT_ROOT && dotnet build"
    echo "3. Run tests: dotnet test"
    echo "4. Start development services: docker-compose up -d"
    echo "5. Run the application: cd src/Honua.Server && dotnet run"
    echo ""
    echo "Useful commands:"
    echo "- Start services: docker-compose up -d"
    echo "- View logs: docker-compose logs -f"
    echo "- Stop services: docker-compose down"
    echo "- Run tests: dotnet test"
    echo "- Check format: dotnet format Honua.sln --verify-no-changes"
    echo "- Pre-PR check: ./scripts/pre-pr-check.sh"
    echo ""
    echo "Development URLs (when running):"
    echo "- Application: http://localhost:8080"
    echo "- Swagger UI: http://localhost:8080/swagger"
    echo "- Health checks: http://localhost:8080/healthz/live"
    echo "- PostgreSQL: localhost:5432 (user: honua_user, password: honua_password)"
    echo ""
    echo "For help, see README.md or run: ./scripts/dev-setup.sh --help"
}

# Function to show help
show_help() {
    echo "Honua Server Development Environment Setup"
    echo ""
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --help                Show this help message"
    echo "  --skip-system         Skip system package installation"
    echo "  --skip-docker         Skip Docker installation"
    echo "  --skip-dotnet         Skip .NET SDK installation"
    echo "  --skip-k8s            Skip Kubernetes tools installation"
    echo "  --validate-only       Only validate existing installation"
    echo ""
    echo "This script installs:"
    echo "  - .NET 10 SDK"
    echo "  - Docker and Docker Compose"
    echo "  - kubectl and Helm"
    echo "  - Development dependencies"
    echo "  - Project-specific setup"
}

# Main function
main() {
    local skip_system=false
    local skip_docker=false
    local skip_dotnet=false
    local skip_k8s=false
    local validate_only=false

    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case $1 in
            --help)
                show_help
                exit 0
                ;;
            --skip-system)
                skip_system=true
                shift
                ;;
            --skip-docker)
                skip_docker=true
                shift
                ;;
            --skip-dotnet)
                skip_dotnet=true
                shift
                ;;
            --skip-k8s)
                skip_k8s=true
                shift
                ;;
            --validate-only)
                validate_only=true
                shift
                ;;
            *)
                echo "Unknown option: $1"
                echo "Use --help for usage information"
                exit 1
                ;;
        esac
    done

    if [[ "$validate_only" == true ]]; then
        validate_installation
        exit $?
    fi

    # Run setup steps
    if [[ "$skip_system" != true ]]; then
        install_system_packages
    fi

    if [[ "$skip_dotnet" != true ]]; then
        install_dotnet
    fi

    if [[ "$skip_docker" != true ]]; then
        install_docker
    fi

    if [[ "$skip_k8s" != true ]]; then
        install_kubectl
    fi

    install_dev_tools
    setup_project_env

    # Validate installation
    echo ""
    if validate_installation; then
        show_next_steps
    else
        echo ""
        echo "❌ Some components failed validation. Please check the errors above."
        echo "You can run './scripts/dev-setup.sh --validate-only' to check again."
        exit 1
    fi
}

# Check if running as root (not recommended)
if [[ $EUID -eq 0 ]]; then
    echo "⚠️  Warning: Running as root is not recommended for development setup"
    echo "Consider running as a regular user with sudo privileges"
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi

main "$@"