#!/bin/bash
set -euo pipefail

# Secret Management Script for Honua Server
# Handles secure secret provisioning for different environments

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_TYPE="${ENV_TYPE:-development}"
VAULT_ADDR="${VAULT_ADDR:-}"
SECRET_BACKEND="${SECRET_BACKEND:-file}"

echo "🔐 Honua Server Secret Management"
echo "Environment: $ENV_TYPE"
echo "Backend: $SECRET_BACKEND"

# Function to validate required tools
check_dependencies() {
    local tools=("jq")

    case "$SECRET_BACKEND" in
        "vault")
            tools+=("vault")
            ;;
        "k8s")
            tools+=("kubectl")
            ;;
        "azure")
            tools+=("az")
            ;;
        "aws")
            tools+=("aws")
            ;;
    esac

    for tool in "${tools[@]}"; do
        if ! command -v "$tool" &> /dev/null; then
            echo "❌ $tool not found. Please install $tool."
            exit 1
        fi
    done
}

# Function to generate secure random passwords
generate_password() {
    local length=${1:-32}
    openssl rand -base64 "$length" | tr -d "=+/" | cut -c1-"$length"
}

# Function to create database secrets
create_database_secrets() {
    local env=$1

    echo "🔑 Creating database secrets for $env..."

    case "$env" in
        "development")
            cat > "/tmp/db-secrets-$env.json" << EOF
{
  "database": {
    "host": "localhost",
    "port": "5432",
    "name": "honua_dev",
    "username": "honua_user",
    "password": "honua_password",
    "ssl_mode": "disable"
  }
}
EOF
            ;;
        "staging"|"production")
            local db_password=$(generate_password 24)
            cat > "/tmp/db-secrets-$env.json" << EOF
{
  "database": {
    "host": "postgres-$env.internal",
    "port": "5432",
    "name": "honua_$env",
    "username": "honua_$env",
    "password": "$db_password",
    "ssl_mode": "require"
  }
}
EOF
            ;;
    esac
}

# Function to create API secrets
create_api_secrets() {
    local env=$1

    echo "🔑 Creating API secrets for $env..."

    local jwt_secret=$(generate_password 64)
    local encryption_key=$(generate_password 32)
    local api_key=$(generate_password 48)

    cat > "/tmp/api-secrets-$env.json" << EOF
{
  "api": {
    "jwt_secret": "$jwt_secret",
    "encryption_key": "$encryption_key",
    "admin_api_key": "$api_key",
    "allowed_origins": ["https://$env.honua.example.com"],
    "cors_enabled": true
  }
}
EOF
}

# Function to create monitoring secrets
create_monitoring_secrets() {
    local env=$1

    echo "🔑 Creating monitoring secrets for $env..."

    local grafana_password=$(generate_password 16)
    local prometheus_token=$(generate_password 32)

    cat > "/tmp/monitoring-secrets-$env.json" << EOF
{
  "monitoring": {
    "grafana_admin_password": "$grafana_password",
    "prometheus_remote_write_token": "$prometheus_token",
    "alert_webhook_url": "https://hooks.slack.com/services/YOUR/SLACK/WEBHOOK"
  }
}
EOF
}

# Function to store secrets in HashiCorp Vault
store_vault_secrets() {
    local env=$1

    echo "🏛️  Storing secrets in Vault for $env..."

    if [[ -z "$VAULT_ADDR" ]]; then
        echo "❌ VAULT_ADDR environment variable not set"
        exit 1
    fi

    # Authenticate to Vault (assumes VAULT_TOKEN is set)
    if ! vault auth -method=token; then
        echo "❌ Failed to authenticate to Vault"
        exit 1
    fi

    # Store database secrets
    vault kv put "secret/honua/$env/database" \
        @"/tmp/db-secrets-$env.json"

    # Store API secrets
    vault kv put "secret/honua/$env/api" \
        @"/tmp/api-secrets-$env.json"

    # Store monitoring secrets
    vault kv put "secret/honua/$env/monitoring" \
        @"/tmp/monitoring-secrets-$env.json"

    echo "✅ Secrets stored in Vault"
}

# Function to store secrets in Kubernetes
store_k8s_secrets() {
    local env=$1
    local namespace="honua-$env"

    echo "☸️  Storing secrets in Kubernetes for $env..."

    # Create namespace if it doesn't exist
    kubectl create namespace "$namespace" --dry-run=client -o yaml | kubectl apply -f -

    # Create database secret
    kubectl create secret generic honua-database \
        --namespace="$namespace" \
        --from-file=config="/tmp/db-secrets-$env.json" \
        --dry-run=client -o yaml | kubectl apply -f -

    # Create API secret
    kubectl create secret generic honua-api \
        --namespace="$namespace" \
        --from-file=config="/tmp/api-secrets-$env.json" \
        --dry-run=client -o yaml | kubectl apply -f -

    # Create monitoring secret
    kubectl create secret generic honua-monitoring \
        --namespace="$namespace" \
        --from-file=config="/tmp/monitoring-secrets-$env.json" \
        --dry-run=client -o yaml | kubectl apply -f -

    echo "✅ Secrets stored in Kubernetes"
}

# Function to store secrets in Azure Key Vault
store_azure_secrets() {
    local env=$1
    local key_vault_name="honua-$env-kv"

    echo "☁️  Storing secrets in Azure Key Vault for $env..."

    # Check if logged in to Azure
    if ! az account show &> /dev/null; then
        echo "❌ Not logged in to Azure. Run 'az login' first."
        exit 1
    fi

    # Store secrets
    local db_password=$(jq -r '.database.password' "/tmp/db-secrets-$env.json")
    local jwt_secret=$(jq -r '.api.jwt_secret' "/tmp/api-secrets-$env.json")
    local encryption_key=$(jq -r '.api.encryption_key' "/tmp/api-secrets-$env.json")

    az keyvault secret set --vault-name "$key_vault_name" \
        --name "honua-db-password" --value "$db_password"

    az keyvault secret set --vault-name "$key_vault_name" \
        --name "honua-jwt-secret" --value "$jwt_secret"

    az keyvault secret set --vault-name "$key_vault_name" \
        --name "honua-encryption-key" --value "$encryption_key"

    echo "✅ Secrets stored in Azure Key Vault"
}

# Function to store secrets in AWS Secrets Manager
store_aws_secrets() {
    local env=$1
    local region="${AWS_DEFAULT_REGION:-us-east-1}"

    echo "☁️  Storing secrets in AWS Secrets Manager for $env..."

    # Check if AWS CLI is configured
    if ! aws sts get-caller-identity &> /dev/null; then
        echo "❌ AWS CLI not configured. Run 'aws configure' first."
        exit 1
    fi

    # Store database secret
    aws secretsmanager create-secret \
        --name "honua/$env/database" \
        --description "Database credentials for Honua Server $env" \
        --secret-string "file:///tmp/db-secrets-$env.json" \
        --region "$region" || \
    aws secretsmanager update-secret \
        --secret-id "honua/$env/database" \
        --secret-string "file:///tmp/db-secrets-$env.json" \
        --region "$region"

    # Store API secret
    aws secretsmanager create-secret \
        --name "honua/$env/api" \
        --description "API credentials for Honua Server $env" \
        --secret-string "file:///tmp/api-secrets-$env.json" \
        --region "$region" || \
    aws secretsmanager update-secret \
        --secret-id "honua/$env/api" \
        --secret-string "file:///tmp/api-secrets-$env.json" \
        --region "$region"

    echo "✅ Secrets stored in AWS Secrets Manager"
}

# Function to generate .env file for local development
generate_env_file() {
    local env=$1

    echo "📄 Generating .env file for $env..."

    local env_file="$SCRIPT_DIR/../.env.$env"

    cat > "$env_file" << EOF
# Honua Server Configuration - $env
# Generated: $(date)

# Database Configuration
DATABASE_HOST=$(jq -r '.database.host' "/tmp/db-secrets-$env.json")
DATABASE_PORT=$(jq -r '.database.port' "/tmp/db-secrets-$env.json")
DATABASE_NAME=$(jq -r '.database.name' "/tmp/db-secrets-$env.json")
DATABASE_USER=$(jq -r '.database.username' "/tmp/db-secrets-$env.json")
DATABASE_PASSWORD=$(jq -r '.database.password' "/tmp/db-secrets-$env.json")
DATABASE_SSL_MODE=$(jq -r '.database.ssl_mode' "/tmp/db-secrets-$env.json")

# API Configuration
JWT_SECRET=$(jq -r '.api.jwt_secret' "/tmp/api-secrets-$env.json")
ENCRYPTION_KEY=$(jq -r '.api.encryption_key' "/tmp/api-secrets-$env.json")
ADMIN_API_KEY=$(jq -r '.api.admin_api_key' "/tmp/api-secrets-$env.json")
ALLOWED_ORIGINS=$(jq -r '.api.allowed_origins[0]' "/tmp/api-secrets-$env.json")

# Monitoring Configuration
GRAFANA_ADMIN_PASSWORD=$(jq -r '.monitoring.grafana_admin_password' "/tmp/monitoring-secrets-$env.json")
PROMETHEUS_REMOTE_WRITE_TOKEN=$(jq -r '.monitoring.prometheus_remote_write_token' "/tmp/monitoring-secrets-$env.json")

# Application Configuration
ASPNETCORE_ENVIRONMENT=$env
ASPNETCORE_URLS=http://+:8080
EOF

    echo "✅ Environment file generated: $env_file"
}

# Function to cleanup temporary files
cleanup() {
    echo "🧹 Cleaning up temporary files..."
    rm -f /tmp/*-secrets-*.json
}

# Function to rotate secrets
rotate_secrets() {
    local env=$1

    echo "🔄 Rotating secrets for $env..."

    # Backup current secrets if they exist
    if [[ -f "/tmp/db-secrets-$env.json" ]]; then
        cp "/tmp/db-secrets-$env.json" "/tmp/db-secrets-$env.json.backup.$(date +%s)"
    fi

    # Generate new secrets
    create_database_secrets "$env"
    create_api_secrets "$env"
    create_monitoring_secrets "$env"

    # Store new secrets
    case "$SECRET_BACKEND" in
        "vault")
            store_vault_secrets "$env"
            ;;
        "k8s")
            store_k8s_secrets "$env"
            ;;
        "azure")
            store_azure_secrets "$env"
            ;;
        "aws")
            store_aws_secrets "$env"
            ;;
        "file")
            generate_env_file "$env"
            ;;
    esac

    echo "✅ Secret rotation completed for $env"
}

# Main function
main() {
    local action=${1:-"generate"}
    local environment=${2:-$ENV_TYPE}

    check_dependencies

    trap cleanup EXIT

    case "$action" in
        "generate")
            echo "🚀 Generating secrets for $environment..."
            create_database_secrets "$environment"
            create_api_secrets "$environment"
            create_monitoring_secrets "$environment"

            case "$SECRET_BACKEND" in
                "vault")
                    store_vault_secrets "$environment"
                    ;;
                "k8s")
                    store_k8s_secrets "$environment"
                    ;;
                "azure")
                    store_azure_secrets "$environment"
                    ;;
                "aws")
                    store_aws_secrets "$environment"
                    ;;
                "file")
                    generate_env_file "$environment"
                    ;;
                *)
                    echo "❌ Unknown secret backend: $SECRET_BACKEND"
                    exit 1
                    ;;
            esac
            ;;
        "rotate")
            rotate_secrets "$environment"
            ;;
        "cleanup")
            cleanup
            ;;
        *)
            echo "Usage: $0 {generate|rotate|cleanup} [environment]"
            echo "Environments: development, staging, production"
            echo "Set SECRET_BACKEND env var: file, vault, k8s, azure, aws"
            exit 1
            ;;
    esac

    echo "🎉 Secret management operation completed successfully!"
}

main "$@"