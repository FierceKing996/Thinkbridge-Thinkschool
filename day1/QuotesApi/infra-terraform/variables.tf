variable "environment" {
  description = "Deployment environment name (dev or prod) - selects the .tfvars file and namespaces every resource so both can exist side by side on the same Docker host."
  type        = string

  validation {
    condition     = contains(["dev", "prod"], var.environment)
    error_message = "environment must be \"dev\" or \"prod\"."
  }
}

variable "api_image_tag" {
  description = "QuotesApi container image tag - produced by `dotnet publish -p:PublishProfile=DefaultContainer` (see ContainerRepository/ContainerImageTag in QuotesApi.csproj)."
  type        = string
}

variable "api_replica_count" {
  description = "Number of API container replicas - 1 for dev, more for prod."
  type        = number
  default     = 1
}

variable "sql_image_tag" {
  type    = string
  default = "2022-latest"
}

# Day 25's "no secret in a committed file" rule applies here too: this has no
# default, is never written into either environments/*.tfvars file, and is
# supplied only via TF_VAR_sql_sa_password at apply time - see
# DAY23_24_IAC.md.
variable "sql_sa_password" {
  description = "SQL Server SA password. Set via TF_VAR_sql_sa_password, never a .tfvars file."
  type        = string
  sensitive   = true
}

variable "jwt_signing_key" {
  description = "Base64-encoded HS256 signing key for QuotesApi's JWT auth (>= 32 bytes decoded). Set via TF_VAR_jwt_signing_key, never a .tfvars file."
  type        = string
  sensitive   = true
}
