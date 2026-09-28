# The "Service Bus" module from the Day 23 brief - RabbitMQ, per Day 19's
# substitution (see DAY19_SERVICE_BUS.md), provisioned as code instead of via
# `docker compose up` for a real environment rather than a local dev machine.

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
variable "network_name" { type = string }

resource "docker_image" "rabbitmq" {
  name = "rabbitmq:4-management-alpine"
}

resource "docker_container" "rabbitmq" {
  name  = "quotesapi-${var.environment}-rabbitmq"
  image = docker_image.rabbitmq.image_id

  # Dev publishes both AMQP and the management UI for local debugging; prod
  # publishes neither - see the network-isolation note in main.tf/DAY27_SECURITY.md.
  dynamic "ports" {
    for_each = var.environment == "dev" ? [
      { internal = 5672, external = 5672 },
      { internal = 15672, external = 15672 },
    ] : []
    content {
      internal = ports.value.internal
      external = ports.value.external
    }
  }

  networks_advanced {
    name = var.network_name
  }
}

output "host" {
  value = docker_container.rabbitmq.name
}
