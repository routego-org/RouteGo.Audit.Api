#!/usr/bin/env bash
# Check what the API needs, build the image, start the API, then wait for /health/ready.
# Usage (on the server): sudo bash ~/audit/deploy/ec2/start.sh
#
# One-server layout: Audit joins the ec2_default network shared by every RouteGo container, so it reaches
# Identity by name and the portal reaches it as http://audit:8080/. There is no SQL database and no
# secret: the store is the DynamoDB table routego-audit-log, reached through the instance role.
set -euo pipefail

cd "$(dirname "$0")"
REGION=ap-south-1
SHARED_NETWORK=ec2_default

if ! docker network inspect "$SHARED_NETWORK" >/dev/null 2>&1; then
  echo "ERROR: Docker network $SHARED_NETWORK not found. Are the other RouteGo containers running?" >&2
  exit 1
fi

# Identity's address and issuer: by name on the shared network, and from Identity's own settings.
IDENTITY_JWKS_URL="${IDENTITY_JWKS_URL:-http://identity:8080/.well-known/jwks.json}"
IDENTITY_ISSUER="${IDENTITY_ISSUER:-$(aws ssm get-parameter --name /routego/prod/identity/issuer --region "$REGION" \
  --query Parameter.Value --output text)}"
AWS_ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)"
export IDENTITY_JWKS_URL IDENTITY_ISSUER AWS_ACCOUNT_ID

# Without its table Audit could not record or read anything, and without its queue it would hear no
# events, so stop here with a clear message instead of a container that restarts forever.
echo ">> Checking the table and the queue the API needs"
if ! aws dynamodb describe-table --table-name routego-audit-log --region "$REGION" >/dev/null 2>&1; then
  echo "ERROR: table routego-audit-log is missing, or this instance role cannot reach it." >&2
  exit 1
fi
if ! aws sqs get-queue-url --queue-name audit-q --region "$REGION" >/dev/null 2>&1; then
  echo "ERROR: queue audit-q is missing, or this instance role cannot reach it." >&2
  exit 1
fi

echo ">> Building image (first time takes 5-10 minutes)"
docker build -t routego/audit:latest ../..

echo ">> Starting the API"
if ! docker compose up -d; then
  echo "ERROR: API start failed. Last logs:" >&2
  docker compose ps -a
  docker compose logs --tail 50 audit
  exit 1
fi

echo ">> Waiting for the API to be ready"
for _ in $(seq 1 30); do
  if curl -fs http://localhost:5009/health/ready >/dev/null 2>&1; then
    docker compose ps -a
    echo ">> DEPLOY OK - Audit API is ready on port 5009"
    exit 0
  fi
  sleep 2
done

echo "ERROR: Audit API did not become ready. Last logs:" >&2
docker compose ps -a
docker compose logs --tail 50 audit
exit 1
