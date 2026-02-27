terraform {
  required_version = ">= 1.5"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 2.0"
    }
  }
}

provider "azurerm" {
  features {}
}

provider "azuread" {}

data "azurerm_subscription" "current" {}

data "azurerm_client_config" "current" {}

locals {
  scope = var.scope != "" ? var.scope : data.azurerm_subscription.current.id
}

resource "azuread_application" "terraform" {
  display_name = var.app_name
}

resource "azuread_service_principal" "terraform" {
  client_id = azuread_application.terraform.client_id
}

resource "azuread_service_principal_password" "terraform" {
  service_principal_id = azuread_service_principal.terraform.object_id
  end_date_relative    = "8760h"
}

resource "azurerm_role_definition" "terraform" {
  name  = var.role_name
  scope = local.scope

  permissions {
    actions = [
      "Microsoft.Resources/subscriptions/resourceGroups/*",
      "Microsoft.ContainerService/managedClusters/*",
      "Microsoft.ContainerService/managedClusters/agentPools/*",
      "Microsoft.Network/virtualNetworks/*",
      "Microsoft.Network/routeTables/*",
      "Microsoft.Network/networkSecurityGroups/*",
      "Microsoft.Network/publicIPAddresses/*",
      "Microsoft.Network/loadBalancers/*",
      "Microsoft.Network/networkInterfaces/*",
      "Microsoft.ManagedIdentity/userAssignedIdentities/*",
      "Microsoft.Insights/diagnosticSettings/*",
      "Microsoft.OperationalInsights/workspaces/*",
      # P1-20: Scoped role assignment actions instead of wildcard.
      # Note: azurerm_role_definition does not support conditions on individual actions.
      # Scope is limited by assignable_scopes below.
      "Microsoft.Authorization/roleAssignments/write",
      "Microsoft.Authorization/roleAssignments/read",
      "Microsoft.Authorization/roleAssignments/delete"
    ]
    not_actions = []
  }

  assignable_scopes = [local.scope]
}

resource "azurerm_role_assignment" "terraform" {
  principal_id       = azuread_service_principal.terraform.object_id
  role_definition_id = azurerm_role_definition.terraform.role_definition_resource_id
  scope              = local.scope
}
