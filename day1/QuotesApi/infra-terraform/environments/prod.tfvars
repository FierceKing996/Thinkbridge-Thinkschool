# Day 23: prod parameters. sql_sa_password is deliberately absent here - see
# variables.tf and DAY23_24_IAC.md; it's supplied only via TF_VAR_sql_sa_password.
environment       = "prod"
api_image_tag     = "0.1.0"
api_replica_count = 3
