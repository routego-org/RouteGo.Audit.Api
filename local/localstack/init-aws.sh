#!/bin/bash
# LocalStack init (LOCAL ONLY): runs inside the LocalStack container when it is ready.
# Creates the audit topic (if this LocalStack does not have it yet), the audit queue with its
# dead-letter queue, and the subscription that delivers every AuditRecorded event to the queue.
# Every command is idempotent, so it is safe against a LocalStack other services already set up.
# Real AWS queues and subscriptions are created once per account by infra, not by this script.
set -euo pipefail

REGION="ap-south-1"
MAX_RECEIVE_COUNT=5   # after 5 attempts a message is parked on audit-dlq

# The topic Identity, Booking, Seat Inventory and Payment publish their audit events to.
audit_topic=$(awslocal sns create-topic --region "$REGION" --name routego-audit-events --query TopicArn --output text)

awslocal sqs create-queue --region "$REGION" --queue-name audit-dlq >/dev/null
dlq_url=$(awslocal sqs get-queue-url --region "$REGION" --queue-name audit-dlq --query QueueUrl --output text)
dlq_arn=$(awslocal sqs get-queue-attributes --region "$REGION" --queue-url "$dlq_url" \
  --attribute-names QueueArn --query Attributes.QueueArn --output text)

awslocal sqs create-queue --region "$REGION" --queue-name audit-q \
  --attributes "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"${MAX_RECEIVE_COUNT}\\\"}\"}" \
  >/dev/null
queue_url=$(awslocal sqs get-queue-url --region "$REGION" --queue-name audit-q --query QueueUrl --output text)
queue_arn=$(awslocal sqs get-queue-attributes --region "$REGION" --queue-url "$queue_url" \
  --attribute-names QueueArn --query Attributes.QueueArn --output text)

# Raw delivery: the queue message body is the RouteGo envelope itself, not an SNS wrapper.
awslocal sns subscribe --region "$REGION" --topic-arn "$audit_topic" --protocol sqs \
  --notification-endpoint "$queue_arn" \
  --attributes '{"RawMessageDelivery":"true","FilterPolicy":"{\"eventType\":[\"AuditRecorded\"]}"}' >/dev/null

echo "created audit-q (DLQ: audit-dlq), subscribed to routego-audit-events"
