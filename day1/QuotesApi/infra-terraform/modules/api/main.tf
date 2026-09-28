# The "API" module from the Day 23 brief - QuotesApi's own container, built by
# the .NET SDK's container support (ContainerRepository/ContainerImageTag in
# QuotesApi.csproj), wired to the database and messaging modules' outputs
# rather than any hardcoded connection info.

# See modules/database/main.tf for why this block has to be repeated in every
# module that uses the docker provider.
terraform {
  required_providers {
    docker = {
      source = "kreuzwerker/docker"
    }
  }
}

variable "environment" { type = string }
variable "image_tag" { type = string }
variable "replica_count" { type = number }
variable "sql_connection_string" {
  type      = string
  sensitive = true
}
variable "rabbitmq_host" { type = string }
variable "network_name" { type = string }

# Same "no secret in a committed file" rule as sql_sa_password - see
# variables.tf and DAY25_IDENTITY.md. Supplied via TF_VAR_jwt_signing_key only.
variable "jwt_signing_key" {
  type      = string
  sensitive = true
}

resource "docker_image" "api" {
  name = "quotes-api:${var.image_tag}"
}

resource "docker_container" "api" {
  count = var.replica_count
  name  = "quotesapi-${var.environment}-api-${count.index}"
  image = docker_image.api.image_id

  env = [
    "ASPNETCORE_ENVIRONMENT=${var.environment == "prod" ? "Production" : "Development"}",
    "Jwt__SigningKey=${var.jwt_signing_key}",
    "RabbitMq__HostName=${var.rabbitmq_host}",
    # The official mcr.microsoft.com/dotnet/aspnet image runs as a non-root
    # user with /app itself owned by root - the app's own shared default
    # ("Data Source=quotes.db", relative to /app) can't be written there at
    # all, container or no container. That default is correct for every
    # other host this app runs on (dotnet run, MonsterASP) and shouldn't
    # change to accommodate one deployment target; this override belongs
    # here, at the infra layer that actually has the constraint. /tmp is
    # writable by any user in a Linux container by default (sticky bit,
    # mode 1777) - fine for a demo container with no volume mounted; a real
    # deployment would mount a volume here instead so data survives a restart.
    "ConnectionStrings__DefaultConnection=Data Source=/tmp/quotes.db",
    # NOT wired to module.database.connection_string on purpose - a real gap,
    # not an oversight: AppDbContext is hardcoded to UseSqlite() in
    # ServiceCollectionExtensions.cs, and QuotesApi.csproj can't reference
    # Microsoft.EntityFrameworkCore.SqlServer + a migrations assembly the way
    # QuotesApi.Migrations.SqlServer does without a circular project reference
    # (that project already references this one). Runtime-switching providers
    # cleanly needs a real refactor (a provider-agnostic migrations story),
    # which is out of scope for "prove the IaC provisions real infra" - so
    # this container runs on its own local SQLite file, and the SQL Server
    # container above is real, running, reachable infrastructure that simply
    # isn't the app's actual datastore yet in this demo.
  ]

  # The one container in this whole stack that's actually meant to be reachable
  # from outside data_tier in both environments - a reverse proxy/ingress would
  # sit in front of this in a real deployment, not shown here.
  ports {
    internal = 8080
    external = 5080 + count.index
  }

  networks_advanced {
    name = var.network_name
  }
}
