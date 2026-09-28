# Day 23 — IaC, and Day 24 — deployment/drift (Terraform instead of Bicep + azd)

Both days are combined here because the substitution decision is the same one:
**Bicep only targets Azure** — there is no "use a free provider instead" version
of Bicep itself, since ARM/Bicep *is* the Azure control plane's own language.
**Terraform** is the natural swap: cloud-agnostic by design, and its own
`plan`/`apply`/`destroy` lifecycle already gives Day 24's "atomic deploy, drift
detection, clean teardown" without needing a separate product the way Azure
needs Deployment Stacks *plus* `azd` on top of plain ARM. The target here is
plain Docker (`kreuzwerker/docker` provider) — free, matches the infra this app
already runs locally in `docker-compose.yml` for Days 19–26 — rather than any
cloud account.

**The old `infra/main.bicep` and `infra/resources.bicep` in this repo predate
this exercise** and target Azure (App Service + whatever else they provision) —
they're vestigial now that the app's actual deployment target is MonsterASP
(see `publish-monsterasp.ps1`) and this exercise deliberately avoids Azure.
Left in place rather than deleted (that's the user's call, not mine to make
unasked), but `infra-terraform/` below is what this exercise actually built and
is the one that matches everything else in Days 19–27.

## The modules

`infra-terraform/main.tf` — three modules (API / database / messaging, matching
the brief's "API, SQL, and Service Bus"), one shared network:

```hcl
resource "docker_network" "data_tier" {
  name = "quotesapi-${var.environment}-data-tier"
}

module "database"  { source = "./modules/database";  ... network_name = docker_network.data_tier.name }
module "messaging" { source = "./modules/messaging"; ... network_name = docker_network.data_tier.name }
module "api"       {
  source                = "./modules/api"
  sql_connection_string = module.database.connection_string
  rabbitmq_host          = module.messaging.host
  network_name           = docker_network.data_tier.name
}
```

`modules/database/main.tf` — a SQL Server container (same image the
`SqlServerContainerFixture` Testcontainers-based test fixture already uses),
parameterized so dev publishes a port and prod doesn't (Day 27's
network-isolation analog, expressed as code rather than left as a manual step):

```hcl
resource "docker_container" "sqlserver" {
  name  = "quotesapi-${var.environment}-sql"
  image = docker_image.sqlserver.image_id
  env   = ["ACCEPT_EULA=Y", "MSSQL_SA_PASSWORD=${var.sa_password}"]

  dynamic "ports" {
    for_each = var.environment == "dev" ? [{ internal = 1433, external = 14330 }] : []
    content { internal = ports.value.internal, external = ports.value.external }
  }

  networks_advanced { name = var.network_name }
}
```

`modules/messaging/main.tf` — RabbitMQ (Day 19's Service Bus substitute), same
dev/prod port-publishing split. `modules/api/main.tf` — the app container
itself, `replica_count` parameterized (1 in dev, 3 in prod), given a real
`Jwt__SigningKey` and `RabbitMq__HostName` (the messaging module's own output -
this container genuinely talks to the RabbitMQ container it was provisioned
alongside). **Not** wired to the database module's connection string, on
purpose - see "What actually happened" below for why.

## Dev/prod parameter files

`environments/dev.tfvars` and `environments/prod.tfvars` differ only in
`environment`, `api_image_tag`, and `api_replica_count` — everything
environment-specific lives in exactly one place per environment, nothing
duplicated across modules. The one variable that's conspicuously **not** in
either file: `sql_sa_password`. Same rule Day 25 established for the JWT
signing key — no secret in a committed file, ever — enforced here by simply
never giving that variable a default and only ever supplying it via
`TF_VAR_sql_sa_password` at apply time (`deploy.ps1` checks for it and refuses
to run without it).

## Deployment Stacks / azd → Terraform's own lifecycle

`infra-terraform/deploy.ps1` is the one-command entry point (`azd up`'s
analog), backed by:

- **Atomic deploy** → `terraform apply` (all managed resources succeed or the
  whole apply rolls back its state changes — no partial half-applied stack).
- **Drift detection** → `terraform plan` — re-running it against an unchanged
  environment reports "No changes", and any manual change made outside
  Terraform (e.g. someone hand-edited a container) shows up as a diff on the
  next plan, the direct analog to what Deployment Stacks flags.
- **Clean teardown** → `terraform destroy` — removes exactly the resources this
  config created (tracked in state), nothing hand-provisioned left dangling.
- **dev vs. prod isolation** → Terraform *workspaces*, one state file per
  environment, so a `dev` plan can never even see `prod`'s resources, let alone
  touch them — `azd`'s "environment" concept, without a separate product.

```powershell
$env:TF_VAR_sql_sa_password = "<generated, not typed into any file>"
./deploy.ps1 -Environment dev
./deploy.ps1 -Environment prod
./deploy.ps1 -Environment dev -Destroy
```

## What actually happened when this was run for real

This was originally written when the working environment had neither Docker
nor Terraform installed, and said so plainly rather than fabricating output.
Both were installed afterward (Docker Desktop's install was itself a small
saga - a broken leftover from an earlier partial install had to be cleared out
first) and the project was actually run against a live Docker engine. Running
it for real found and fixed **four bugs** no amount of reading the `.tf` files
would have caught:

1. **Child modules don't inherit the provider source.** `terraform init`
   failed immediately with "provider registry.terraform.io does not have a
   provider named registry.terraform.io/hashicorp/docker" - each of the three
   modules needed its own `required_providers { docker = { source =
   "kreuzwerker/docker" } } }` block. A child module inherits the *provider
   configuration* (the connection details) from the root automatically, but
   not the *source address* naming which registry entry that provider even
   is - that has to be declared everywhere the provider is used.
2. **The API container had no `Jwt__SigningKey`.** It crashed on boot with
   `ArgumentNullException` the instant `AddJwtAuth` tried to Base64-decode a
   null. Fixed by adding a `jwt_signing_key` variable with the exact same "no
   default, no .tfvars, `TF_VAR_jwt_signing_key` only" treatment as
   `sql_sa_password`.
3. **`/app` is owned by root in the official container image.** `Program.cs`
   unconditionally tried to create an `App_Data/` directory under the app's
   content root at startup - harmless under `dotnet run` (your own user owns
   the working directory) but `UnauthorizedAccessException` under the
   official `mcr.microsoft.com/dotnet/aspnet` image, which runs as an
   unprivileged non-root user by design. Fixed in `Program.cs` itself: the
   directory is now derived from the *actual* configured connection string
   and only created if that connection string genuinely points somewhere
   with a directory component - Development's own default (`Data
   Source=quotes.db`, no directory at all) now correctly creates nothing.
4. **Same root cause, one level up.** Fixing #3 surfaced that even a bare
   `quotes.db` file couldn't be created directly under `/app` either -
   `SQLite Error 14: unable to open database file`. This one didn't belong in
   the app's shared default (that default is correct for every *other* host
   this app runs on); it belongs at the infra layer that actually has the
   constraint, so `modules/api/main.tf` overrides
   `ConnectionStrings__DefaultConnection` to `Data Source=/tmp/quotes.db` -
   `/tmp` is writable by any user in a Linux container by default.

**The real run, condensed** (see the conversation history for the full,
unedited terminal output - this is the shape of it, not a re-typed transcript):

```
$ terraform init
Installing kreuzwerker/docker v3.9.0...
Terraform has been successfully initialized!

$ terraform validate
Success! The configuration is valid.

$ terraform plan -var-file="environments/dev.tfvars" -out=tfplan
Plan: 7 to add, 0 to change, 0 to destroy.

$ terraform apply "tfplan"
Apply complete! Resources: 7 added, 0 changed, 0 destroyed.

$ docker ps
NAMES                    STATUS          PORTS
quotesapi-dev-api-0      Up             0.0.0.0:5080->8080/tcp
quotesapi-dev-rabbitmq   Up             0.0.0.0:5672->5672/tcp, 0.0.0.0:15672->15672/tcp
quotesapi-dev-sql        Up             0.0.0.0:14330->1433/tcp

$ curl http://localhost:5080/health
200

# re-running plan against a stack that already matches:
$ terraform plan -var-file="environments/dev.tfvars"
No changes. Your infrastructure matches the configuration.
```

That last line is genuine drift detection, not a simulation: Terraform
re-read the real state of every container/image/network from the live Docker
engine and confirmed it still matched `main.tf`.

**Proof the containers actually do something, not just exist:** a quote was
created through the running API container
(`http://localhost:5080/api/quotes`), and its logs showed the full Day
18-20 chain firing for real against the Terraform-provisioned RabbitMQ
container - the outbox relay published it, and both `"3 competing consumers
listening on quotes.search-index"` and the audit-log consumer picked it up
and recorded it, entirely inside infrastructure `terraform apply` created
from nothing.

**The one thing still not wired up, honestly:** the API container's database
is its own local SQLite file, not the SQL Server container standing right
next to it. `AppDbContext` is hardcoded to `UseSqlite()`, and giving it a
real runtime SQL-Server-vs-SQLite switch cleanly needs
`Microsoft.EntityFrameworkCore.SqlServer` added to `QuotesApi.csproj` plus a
migrations-assembly story that doesn't create a circular project reference
with `QuotesApi.Migrations.SqlServer` - a real, scoped-out piece of follow-up
work, not something quietly glossed over. The SQL Server container is real,
healthy, provisioned infrastructure; it's just not the app's actual datastore
yet in this demo.
