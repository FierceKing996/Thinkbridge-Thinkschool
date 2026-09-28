# The "SQL" module from the Day 23 brief: a SQL Server container, parameterized
# by environment. Same image/edition the SqlServerContainerFixture test fixture
# (Quotes.Tests.Integration) already spins up via Testcontainers - this is the
# same infra, provisioned declaratively instead of from test setup code.

# Every module using a non-default-namespace provider (kreuzwerker/docker,
# not hashicorp/docker) must declare that source itself - a child module does
# NOT inherit the root module's source mapping, only its provider
# *configuration*. Without this block, `terraform init` looks up
# hashicorp/docker (which doesn't exist) instead of kreuzwerker/docker.
terraform {
  required_providers {
    docker = {
      source = "kreuzwerker/docker"
    }
  }
}

variable "environment" { type = string }
variable "sa_password" {
  type      = string
  sensitive = true
}
variable "image_tag" { type = string }
variable "network_name" { type = string }

resource "docker_image" "sqlserver" {
  name = "mcr.microsoft.com/mssql/server:${var.image_tag}"
}

resource "docker_container" "sqlserver" {
  name  = "quotesapi-${var.environment}-sql"
  image = docker_image.sqlserver.image_id

  env = [
    "ACCEPT_EULA=Y",
    "MSSQL_SA_PASSWORD=${var.sa_password}",
  ]

  # Day 27's private-endpoint analog: dev publishes a port so a developer can
  # connect a SQL client directly; prod does not - the API container (on the
  # same data_tier network) is the only thing that can ever reach this.
  dynamic "ports" {
    for_each = var.environment == "dev" ? [{ internal = 1433, external = 14330 }] : []
    content {
      internal = ports.value.internal
      external = ports.value.external
    }
  }

  networks_advanced {
    name = var.network_name
  }
}

output "connection_string" {
  value     = "Server=quotesapi-${var.environment}-sql,1433;Database=QuotesApi;User Id=sa;Password=${var.sa_password};TrustServerCertificate=True;"
  sensitive = true
}
