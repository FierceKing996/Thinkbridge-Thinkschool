<#
.SYNOPSIS
    Day 24: single-command deploy, the free/self-hosted analog to `azd up`.

.DESCRIPTION
    Azure Deployment Stacks give you atomic, drift-detectable deployments plus
    clean teardown, driven by `azd`. Terraform gives you the same three
    properties natively, no extra product needed on top of it:

      - Atomic apply         -> `terraform apply` (all-or-nothing per run)
      - Drift detection      -> `terraform plan` (diffs real state vs. desired)
      - Clean teardown       -> `terraform destroy` (removes exactly what this
                                 config created - nothing hand-provisioned lingers)

    Workspaces are what `azd`'s environment concept maps to here: `dev` and
    `prod` are two separate state files, so a plan/apply against one can never
    accidentally touch the other's resources.

.PARAMETER Environment
    "dev" or "prod" - selects both the Terraform workspace and the matching
    environments/<name>.tfvars file.

.PARAMETER Destroy
    Tear down that environment's resources instead of deploying them.
#>
param(
    [Parameter(Mandatory)][ValidateSet("dev", "prod")][string]$Environment,
    [switch]$Destroy
)

$ErrorActionPreference = "Stop"

if (-not $env:TF_VAR_sql_sa_password) {
    throw "TF_VAR_sql_sa_password is not set. Per DAY23_24_IAC.md, this is never in a .tfvars file - export it (a real secrets manager in CI) before running this script."
}
if (-not $env:TF_VAR_jwt_signing_key) {
    throw "TF_VAR_jwt_signing_key is not set. Same rule - export it before running this script."
}

terraform init

terraform workspace select $Environment 2>$null
if ($LASTEXITCODE -ne 0) {
    terraform workspace new $Environment
}

if ($Destroy) {
    terraform destroy -var-file="environments/$Environment.tfvars"
    return
}

# plan -out first: what actually gets applied is the exact plan just reviewed,
# not a second, potentially different plan computed at apply time - the same
# "what-if, then deploy that reviewed change" flow Deployment Stacks give you.
terraform plan -var-file="environments/$Environment.tfvars" -out=tfplan
terraform apply tfplan
