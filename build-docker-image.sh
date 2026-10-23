#!/bin/bash
set -e

docker build -f Dockerfile --tag raven-hosting:local .
