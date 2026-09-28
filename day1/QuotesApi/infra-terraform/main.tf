# Day 23: Terraform in place of Bicep - Bicep only targets Azure by definition,
# so there's no way to "use a free provider instead" and still call it Bicep.
# Terraform is itself cloud-agnostic; the kreuzwerker/docker provider below
# targets plain local Docker, which is free and matches the infra this app
# already uses in docker-compose.yml for local dev (Days 19-26) - this is that
# same infra, described declaratively instead of imperatively.
terraform {
  required_version = ">= 1.7.0"

  required_providers {
    docker = {
      source  = "kreuzwerker/docker"
      version = "~> 3.0"
    }
  }
}

provider "docker" {}

# Day 27's private-endpoint analog, provisioned as code rather than left as a
# manual `docker network create` step - see modules/*/main.tf for how "no
# published port in prod" is expressed per-resource.
resource "docker_network" "data_tier" {
  name = "quotesapi-${var.environment}-data-tier"
}

module "database" {
  source      = "./modules/database"
  environment = var.environment
  sa_password = var.sql_sa_password
  image_tag   = var.sql_image_tag
  network_name = docker_network.data_tier.name
}

module "messaging" {
  source       = "./modules/messaging"
  environment  = var.environment
  network_name = docker_network.data_tier.name
}

module "api" {
  source                 = "./modules/api"
  environment            = var.environment
  image_tag              = var.api_image_tag
  replica_count          = var.api_replica_count
  sql_connection_string  = module.database.connection_string
  rabbitmq_host          = module.messaging.host
  network_name           = docker_network.data_tier.name
  jwt_signing_key        = var.jwt_signing_key
}
