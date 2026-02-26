variable "name_prefix" {
  description = "Name prefix for resources."
  type        = string
  default     = "honua"
}

variable "environment" {
  description = "Environment suffix."
  type        = string
  default     = "it"
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "tags" {
  description = "Additional tags."
  type        = map(string)
  default     = {}
}

variable "node_count" {
  description = "AKS system node count."
  type        = number
  default     = 2
}

variable "node_vm_size" {
  description = "AKS node VM size."
  type        = string
  default     = "Standard_B2s"
}

variable "node_os_disk_size_gb" {
  description = "Node OS disk size in GB."
  type        = number
  default     = 64
}

variable "kubernetes_version" {
  description = "Optional AKS version override."
  type        = string
  default     = ""
}

variable "sku_tier" {
  description = "AKS SKU tier."
  type        = string
  default     = "Free"
}
