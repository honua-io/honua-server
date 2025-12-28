# Honua Server Infrastructure as Code
# Terraform configuration for deploying Honua Server to cloud platforms

terraform {
  required_version = ">= 1.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "~> 2.20"
    }
    helm = {
      source  = "hashicorp/helm"
      version = "~> 2.10"
    }
  }

  backend "s3" {
    # Configure remote state storage
    bucket = "honua-terraform-state"
    key    = "honua-server/terraform.tfstate"
    region = "us-east-1"

    # Enable state locking
    dynamodb_table = "honua-terraform-locks"
    encrypt        = true
  }
}

# Variables
variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
  default     = "dev"
}

variable "region" {
  description = "AWS region"
  type        = string
  default     = "us-east-1"
}

variable "cluster_name" {
  description = "EKS cluster name"
  type        = string
  default     = "honua-cluster"
}

variable "node_group_instance_types" {
  description = "EC2 instance types for EKS node group"
  type        = list(string)
  default     = ["t3.medium"]
}

variable "node_group_desired_size" {
  description = "Desired number of nodes in EKS node group"
  type        = number
  default     = 2
}

variable "node_group_max_size" {
  description = "Maximum number of nodes in EKS node group"
  type        = number
  default     = 10
}

variable "node_group_min_size" {
  description = "Minimum number of nodes in EKS node group"
  type        = number
  default     = 1
}

variable "db_instance_class" {
  description = "RDS instance class"
  type        = string
  default     = "db.t3.micro"
}

variable "db_allocated_storage" {
  description = "RDS allocated storage in GB"
  type        = number
  default     = 20
}

# Data sources
data "aws_availability_zones" "available" {
  state = "available"
}

data "aws_caller_identity" "current" {}

# Local values
locals {
  cluster_name = "${var.cluster_name}-${var.environment}"
  common_tags = {
    Environment = var.environment
    Project     = "honua-server"
    ManagedBy   = "terraform"
  }
}

# VPC
module "vpc" {
  source = "terraform-aws-modules/vpc/aws"

  name = "honua-vpc-${var.environment}"
  cidr = "10.0.0.0/16"

  azs             = slice(data.aws_availability_zones.available.names, 0, 3)
  private_subnets = ["10.0.1.0/24", "10.0.2.0/24", "10.0.3.0/24"]
  public_subnets  = ["10.0.101.0/24", "10.0.102.0/24", "10.0.103.0/24"]

  enable_nat_gateway = true
  enable_vpn_gateway = false
  enable_dns_hostnames = true
  enable_dns_support = true

  tags = merge(local.common_tags, {
    Name = "honua-vpc-${var.environment}"
  })
}

# EKS Cluster
module "eks" {
  source = "terraform-aws-modules/eks/aws"

  cluster_name    = local.cluster_name
  cluster_version = "1.27"

  vpc_id     = module.vpc.vpc_id
  subnet_ids = module.vpc.private_subnets

  # EKS Managed Node Groups
  eks_managed_node_groups = {
    main = {
      name = "honua-nodes-${var.environment}"

      instance_types = var.node_group_instance_types

      min_size     = var.node_group_min_size
      max_size     = var.node_group_max_size
      desired_size = var.node_group_desired_size

      # Launch template configuration
      launch_template_name = "honua-node-template-${var.environment}"
      description     = "EKS managed node group for Honua Server"

      pre_bootstrap_user_data = <<-EOT
      #!/bin/bash
      yum update -y
      EOT

      # Node group security
      vpc_security_group_ids = [aws_security_group.node_group_sg.id]

      tags = local.common_tags
    }
  }

  # Cluster access entry
  # To add the current caller identity as an administrator
  enable_cluster_creator_admin_permissions = true

  tags = local.common_tags
}

# Security Group for EKS Node Group
resource "aws_security_group" "node_group_sg" {
  name_prefix = "honua-node-group-${var.environment}-"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description = "HTTP"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  ingress {
    description = "HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  ingress {
    description = "Honua Server"
    from_port   = 8080
    to_port     = 8080
    protocol    = "tcp"
    cidr_blocks = [module.vpc.vpc_cidr_block]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = merge(local.common_tags, {
    Name = "honua-node-group-sg-${var.environment}"
  })
}

# RDS PostgreSQL Database
module "rds" {
  source = "terraform-aws-modules/rds/aws"

  identifier = "honua-postgres-${var.environment}"

  engine               = "postgres"
  engine_version       = "15.3"
  family               = "postgres15"
  major_engine_version = "15"
  instance_class       = var.db_instance_class

  allocated_storage     = var.db_allocated_storage
  max_allocated_storage = 100
  storage_encrypted     = true

  db_name  = "honua_${var.environment}"
  username = "honua_admin"
  port     = 5432

  # Password will be generated automatically and stored in AWS Secrets Manager
  manage_master_user_password = true

  vpc_security_group_ids = [aws_security_group.rds_sg.id]
  subnet_ids            = module.vpc.private_subnets

  # Backup configuration
  backup_retention_period = var.environment == "prod" ? 7 : 3
  backup_window          = "03:00-04:00"
  maintenance_window     = "Sun:04:00-Sun:05:00"

  # Enhanced monitoring
  monitoring_interval    = "60"
  monitoring_role_name   = "honua-rds-monitoring-role-${var.environment}"
  create_monitoring_role = true

  # Performance insights
  performance_insights_enabled = var.environment == "prod" ? true : false

  tags = local.common_tags
}

# Security Group for RDS
resource "aws_security_group" "rds_sg" {
  name_prefix = "honua-rds-${var.environment}-"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description     = "PostgreSQL"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.node_group_sg.id]
  }

  tags = merge(local.common_tags, {
    Name = "honua-rds-sg-${var.environment}"
  })
}

# Application Load Balancer
resource "aws_lb" "main" {
  name               = "honua-alb-${var.environment}"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb_sg.id]
  subnets           = module.vpc.public_subnets

  enable_deletion_protection = var.environment == "prod" ? true : false

  tags = local.common_tags
}

# Security Group for ALB
resource "aws_security_group" "alb_sg" {
  name_prefix = "honua-alb-${var.environment}-"
  vpc_id      = module.vpc.vpc_id

  ingress {
    description = "HTTP"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = merge(local.common_tags, {
    Name = "honua-alb-sg-${var.environment}"
  })
}

# AWS Load Balancer Controller IAM Role
module "load_balancer_controller_irsa_role" {
  source = "terraform-aws-modules/iam/aws//modules/iam-role-for-service-accounts-eks"

  role_name = "aws-load-balancer-controller-${var.environment}"

  attach_load_balancer_controller_policy = true

  oidc_providers = {
    ex = {
      provider_arn               = module.eks.oidc_provider_arn
      namespace_service_accounts = ["kube-system:aws-load-balancer-controller"]
    }
  }

  tags = local.common_tags
}

# Secrets Manager for application secrets
resource "aws_secretsmanager_secret" "app_secrets" {
  name_prefix = "honua-server-${var.environment}-"
  description = "Application secrets for Honua Server ${var.environment}"

  tags = local.common_tags
}

# IAM role for Honua Server pods
module "honua_server_irsa_role" {
  source = "terraform-aws-modules/iam/aws//modules/iam-role-for-service-accounts-eks"

  role_name = "honua-server-${var.environment}"

  role_policy_arns = {
    secretsmanager = aws_iam_policy.honua_secrets_policy.arn
  }

  oidc_providers = {
    ex = {
      provider_arn               = module.eks.oidc_provider_arn
      namespace_service_accounts = ["honua-${var.environment}:honua-server"]
    }
  }

  tags = local.common_tags
}

# IAM policy for accessing secrets
resource "aws_iam_policy" "honua_secrets_policy" {
  name_prefix = "honua-secrets-${var.environment}-"
  path        = "/"
  description = "IAM policy for Honua Server to access secrets"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "secretsmanager:GetSecretValue",
          "secretsmanager:DescribeSecret"
        ]
        Resource = [
          aws_secretsmanager_secret.app_secrets.arn,
          module.rds.db_instance_master_user_secret_arn
        ]
      }
    ]
  })

  tags = local.common_tags
}

# Outputs
output "cluster_endpoint" {
  description = "Endpoint for EKS control plane"
  value       = module.eks.cluster_endpoint
}

output "cluster_security_group_id" {
  description = "Security group ids attached to the cluster control plane"
  value       = module.eks.cluster_security_group_id
}

output "cluster_iam_role_name" {
  description = "IAM role name associated with EKS cluster"
  value       = module.eks.cluster_iam_role_name
}

output "cluster_certificate_authority_data" {
  description = "Base64 encoded certificate data required to communicate with the cluster"
  value       = module.eks.cluster_certificate_authority_data
}

output "cluster_name" {
  description = "The name/id of the EKS cluster"
  value       = module.eks.cluster_name
}

output "rds_endpoint" {
  description = "RDS instance endpoint"
  value       = module.rds.db_instance_endpoint
  sensitive   = true
}

output "rds_master_user_secret_arn" {
  description = "ARN of the master user secret"
  value       = module.rds.db_instance_master_user_secret_arn
  sensitive   = true
}

output "load_balancer_dns_name" {
  description = "DNS name of the load balancer"
  value       = aws_lb.main.dns_name
}