#!/bin/bash
# Runs the local image on 127.0.0.1:5246 with throwaway dev keys and a database in ./data
set -e

docker run \
  --rm \
  --name raven-hosting \
  --env ASPNETCORE_ENVIRONMENT=production \
  --env RavenHosting__SignupApiKey=dev-signup-key \
  --env RavenHosting__CreateApiKey=dev-create-key \
  -v "$(pwd)/data:/data" \
  -p 127.0.0.1:5246:8080 \
  raven-hosting:local
