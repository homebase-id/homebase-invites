#!/bin/bash
set -e

docker build -f Dockerfile --tag homebase-invites:local .
