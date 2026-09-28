# Day 23: dev parameters. sql_sa_password is deliberately absent here - see
# variables.tf and DAY23_24_IAC.md; it's supplied only via TF_VAR_sql_sa_password.
environment       = "dev"
api_image_tag     = "0.1.0-dev"
api_replica_count = 1
