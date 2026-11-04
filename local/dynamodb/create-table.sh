#!/bin/bash
# Creates the audit table on DynamoDB Local (LOCAL ONLY), the same shape the deployment uses:
#   pk / sk     the entity's history (pk = ENTITY#<type>#<id>, sk = <time>#<eventId>) and the event markers
#   gsi1        the business-day list (gsi1pk = DAY#<yyyy-mm-dd, India time>, gsi1sk = <time>#<eventId>)
# Idempotent: does nothing when the table already exists.
#   docker compose up -d dynamodb && bash local/dynamodb/create-table.sh
set -euo pipefail

ENDPOINT="${DYNAMODB_ENDPOINT:-http://localhost:8000}"
TABLE="routego-audit-log"
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-local}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-local}"
export AWS_DEFAULT_REGION="ap-south-1"

if aws dynamodb describe-table --endpoint-url "$ENDPOINT" --table-name "$TABLE" >/dev/null 2>&1; then
  echo "$TABLE already exists"
  exit 0
fi

aws dynamodb create-table --endpoint-url "$ENDPOINT" --table-name "$TABLE" \
  --billing-mode PAY_PER_REQUEST \
  --attribute-definitions AttributeName=pk,AttributeType=S AttributeName=sk,AttributeType=S \
                          AttributeName=gsi1pk,AttributeType=S AttributeName=gsi1sk,AttributeType=S \
  --key-schema AttributeName=pk,KeyType=HASH AttributeName=sk,KeyType=RANGE \
  --global-secondary-indexes \
    'IndexName=gsi1,KeySchema=[{AttributeName=gsi1pk,KeyType=HASH},{AttributeName=gsi1sk,KeyType=RANGE}],Projection={ProjectionType=ALL}' \
  >/dev/null

echo "created $TABLE on $ENDPOINT"
