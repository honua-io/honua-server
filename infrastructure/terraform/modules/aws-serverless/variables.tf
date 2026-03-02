variable "name_prefix" {
  description = "Name prefix for resources."
  type        = string
  default     = "honua"
}

variable "environment" {
  description = "Environment name (dev, staging, prod)."
  type        = string
  default     = "dev"
}

variable "tags" {
  description = "Additional tags to apply to resources."
  type        = map(string)
  default     = {}
}

variable "vpc_cidr" {
  description = "CIDR block for the VPC."
  type        = string
  default     = "10.0.0.0/16"
}

variable "public_subnet_cidrs" {
  description = "CIDR blocks for public subnets."
  type        = list(string)
  default     = ["10.0.101.0/24", "10.0.102.0/24", "10.0.103.0/24"]
}

variable "private_subnet_cidrs" {
  description = "CIDR blocks for private subnets."
  type        = list(string)
  default     = ["10.0.1.0/24", "10.0.2.0/24", "10.0.3.0/24"]
}

variable "existing_vpc_id" {
  description = "Existing VPC ID to reuse instead of creating a new VPC."
  type        = string
  default     = ""
}

variable "existing_vpc_cidr" {
  description = "CIDR block for existing_vpc_id. Required when reusing a VPC."
  type        = string
  default     = ""
}

variable "existing_public_subnet_ids" {
  description = "Public subnet IDs in existing_vpc_id. Required when reusing a VPC."
  type        = list(string)
  default     = []
}

variable "existing_private_subnet_ids" {
  description = "Private subnet IDs in existing_vpc_id. Required when reusing a VPC."
  type        = list(string)
  default     = []
}

variable "enable_nat_gateway" {
  description = "Whether to provision NAT gateways for private subnets."
  type        = bool
  default     = true
}

variable "image" {
  description = "Lambda container image URI (ECR). Prefer Honua Lambda AOT tags (`vX.Y.Z-lambda-aot`); JIT tags (`vX.Y.Z-lambda`) are debug fallback."
  type        = string
}

variable "lambda_memory_size" {
  description = "Lambda memory size in MB."
  type        = number
  default     = 1024
}

variable "lambda_timeout_seconds" {
  description = "Lambda timeout in seconds. API Gateway HTTP API has a 30-second integration timeout ceiling."
  type        = number
  default     = 30
  validation {
    condition     = var.lambda_timeout_seconds >= 10 && var.lambda_timeout_seconds <= 900
    error_message = "Lambda timeout must be between 10 and 900 seconds."
  }
}

variable "lambda_ephemeral_storage_mb" {
  description = "Lambda ephemeral storage size in MB."
  type        = number
  default     = 512
}

variable "lambda_architectures" {
  description = "Lambda architectures (x86_64 or arm64)."
  type        = list(string)
  default     = ["x86_64"]
}

variable "lambda_reserved_concurrent_executions" {
  description = "Reserved concurrency limit for the Lambda function (null for unreserved)."
  type        = number
  default     = null
}

variable "admin_password" {
  description = "Admin API password for Honua (required in non-dev)."
  type        = string
  sensitive   = true
  validation {
    condition     = length(var.admin_password) >= 32
    error_message = "admin_password must be at least 32 characters (it is also used as Security__ConnectionEncryption__MasterKey)."
  }
}

variable "skip_migrations" {
  description = "Skip database migrations on startup."
  type        = bool
  default     = true
}

variable "db_username" {
  description = "PostgreSQL admin username."
  type        = string
  default     = "honua"
}

variable "db_password" {
  description = "PostgreSQL admin password. Leave null to auto-generate."
  type        = string
  sensitive   = true
  default     = null
}

variable "db_name" {
  description = "PostgreSQL database name."
  type        = string
  default     = "honua"
}

variable "db_instance_class" {
  description = "RDS instance class."
  type        = string
  default     = "db.t3.micro"
}

variable "db_allocated_storage" {
  description = "RDS allocated storage in GB."
  type        = number
  default     = 20
}

variable "db_publicly_accessible" {
  description = "Whether the RDS instance is publicly accessible."
  type        = bool
  default     = false
}

variable "db_additional_ingress_cidrs" {
  description = "Additional CIDRs allowed to access PostgreSQL (for controlled migration/PostGIS operations)."
  type        = list(string)
  default     = []
}

variable "db_multi_az" {
  description = "Enable Multi-AZ for RDS."
  type        = bool
  default     = false
}

variable "db_require_ssl" {
  description = "Append SSL requirements to the connection string."
  type        = bool
  default     = true
}

variable "existing_db_endpoint" {
  description = "Existing PostgreSQL endpoint to reuse. Set with existing_db_connection_string."
  type        = string
  default     = ""
}

variable "existing_db_connection_string" {
  description = "Existing PostgreSQL connection string to reuse. Set with existing_db_endpoint."
  type        = string
  default     = ""
  sensitive   = true
}

variable "enable_postgis" {
  description = "Attempt to enable PostGIS and PostGIS Raster via local-exec (requires psql + network access)."
  type        = bool
  default     = false
}

variable "postgis_readiness_max_attempts" {
  description = "Maximum readiness attempts before PostGIS enablement fails."
  type        = number
  default     = 90

  validation {
    condition     = var.postgis_readiness_max_attempts >= 1
    error_message = "postgis_readiness_max_attempts must be at least 1."
  }
}

variable "postgis_readiness_sleep_seconds" {
  description = "Seconds to sleep between PostgreSQL readiness attempts."
  type        = number
  default     = 10

  validation {
    condition     = var.postgis_readiness_sleep_seconds >= 1
    error_message = "postgis_readiness_sleep_seconds must be at least 1."
  }
}

variable "additional_env" {
  description = "Additional environment variables for the Lambda function."
  type        = map(string)
  default     = {}
}

variable "redis_connection_string" {
  description = "Redis connection string for multi-node mode. Leave empty to create Redis."
  type        = string
  default     = ""
  sensitive   = true
}

variable "redis_auth_token" {
  description = "Redis auth token (used when creating Redis). Leave empty to auto-generate."
  type        = string
  default     = ""
  sensitive   = true
}

variable "redis_enabled" {
  description = "Provision Redis (ElastiCache) for multi-node mode."
  type        = bool
  default     = true
}

variable "redis_node_type" {
  description = "ElastiCache node type."
  type        = string
  default     = "cache.t3.micro"
}

variable "redis_engine_version" {
  description = "Redis engine version."
  type        = string
  default     = "7.0"
}

variable "redis_parameter_group_name" {
  description = "Redis parameter group name."
  type        = string
  default     = "default.redis7"
}

variable "redis_num_cache_clusters" {
  description = "Number of cache clusters in the replication group. Use 1 for lowest-cost validation; use >=2 for HA failover."
  type        = number
  default     = 1
}

variable "redis_port" {
  description = "Redis port."
  type        = number
  default     = 6379
}

variable "log_retention_days" {
  description = "CloudWatch log retention in days."
  type        = number
  default     = 365
}

variable "single_nat_gateway" {
  description = "Use a single NAT gateway instead of one per AZ (cost savings for non-prod)."
  type        = bool
  default     = true
}

variable "api_throttle_burst_limit" {
  description = "API Gateway burst throttle limit (requests)."
  type        = number
  default     = 100
}

variable "api_throttle_rate_limit" {
  description = "API Gateway steady-state throttle limit (requests per second)."
  type        = number
  default     = 50
}

variable "cors_allowed_origins" {
  description = "List of allowed CORS origins. Set to null to disable CORS."
  type        = list(string)
  default     = null
}

variable "db_engine_version" {
  description = "PostgreSQL engine version for RDS."
  type        = string
  default     = "15"
}

variable "serve_admin_ui" {
  description = "Whether to serve the Honua admin UI."
  type        = bool
  default     = false
}
