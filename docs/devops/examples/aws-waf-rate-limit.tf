###############################################################################
# Honua edge rate-limiting template (AWS ALB + WAFv2)
#
# Usage:
# 1. Copy into your Terraform stack.
# 2. Set `alb_arn` to the ALB that fronts Honua Server.
# 3. `terraform apply` to attach the ACL to the ALB.
###############################################################################

variable "name_prefix" {
  description = "Resource name prefix."
  type        = string
  default     = "honua"
}

variable "alb_arn" {
  description = "ARN of the Application Load Balancer that fronts Honua Server."
  type        = string
}

variable "api_limit_per_5m" {
  description = "Per-IP request limit per 5 minutes for /rest, /ogc, and /odata."
  type        = number
  default     = 2000
}

variable "admin_limit_per_5m" {
  description = "Per-IP request limit per 5 minutes for /admin."
  type        = number
  default     = 300
}

variable "tags" {
  description = "Additional tags applied to WAF resources."
  type        = map(string)
  default     = {}
}

resource "aws_wafv2_regex_pattern_set" "honua_api_paths" {
  name  = "${var.name_prefix}-api-paths"
  scope = "REGIONAL"

  regular_expression {
    regex_string = "^/(rest|ogc|odata)/"
  }
}

resource "aws_wafv2_web_acl" "honua_rate_limit" {
  name        = "${var.name_prefix}-rate-limit"
  description = "Rate-limit policy for Honua edge endpoints."
  scope       = "REGIONAL"

  default_action {
    allow {}
  }

  custom_response_body {
    key          = "rate-limited"
    content_type = "APPLICATION_JSON"
    content      = "{\"error\":\"rate_limited\",\"message\":\"Too many requests. Retry later.\"}"
  }

  rule {
    name     = "api-rate-limit"
    priority = 1

    action {
      block {
        custom_response {
          response_code            = 429
          custom_response_body_key = "rate-limited"
          response_header {
            name  = "Retry-After"
            value = "60"
          }
        }
      }
    }

    statement {
      rate_based_statement {
        limit              = var.api_limit_per_5m
        aggregate_key_type = "IP"

        scope_down_statement {
          regex_pattern_set_reference_statement {
            arn = aws_wafv2_regex_pattern_set.honua_api_paths.arn

            field_to_match {
              uri_path {}
            }

            text_transformation {
              priority = 0
              type     = "NONE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${var.name_prefix}-api-rate-limit"
      sampled_requests_enabled   = true
    }
  }

  rule {
    name     = "admin-rate-limit"
    priority = 2

    action {
      block {
        custom_response {
          response_code            = 429
          custom_response_body_key = "rate-limited"
          response_header {
            name  = "Retry-After"
            value = "60"
          }
        }
      }
    }

    statement {
      rate_based_statement {
        limit              = var.admin_limit_per_5m
        aggregate_key_type = "IP"

        scope_down_statement {
          byte_match_statement {
            positional_constraint = "STARTS_WITH"
            search_string         = "/admin/"

            field_to_match {
              uri_path {}
            }

            text_transformation {
              priority = 0
              type     = "NONE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${var.name_prefix}-admin-rate-limit"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "${var.name_prefix}-rate-limit"
    sampled_requests_enabled   = true
  }

  tags = var.tags
}

resource "aws_wafv2_web_acl_association" "honua_alb" {
  resource_arn = var.alb_arn
  web_acl_arn  = aws_wafv2_web_acl.honua_rate_limit.arn
}

output "waf_web_acl_arn" {
  description = "Web ACL ARN for module wiring and diagnostics."
  value       = aws_wafv2_web_acl.honua_rate_limit.arn
}
