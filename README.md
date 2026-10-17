# InboundFlow.WebhookAPI

Lightweight **C# (.NET 10) ASP.NET Core Minimal API** and **Entity Framework Core (SQLite)** webhook receiver engineered to capture missed inbound phone calls from Twilio and queue automated SMS lead recovery for B2B service contractors.

## Architecture & Tech Stack
- **Runtime:** .NET 10 / ASP.NET Core Minimal API
- **ORM & Persistence:** Entity Framework Core with local SQLite (inboundflow.db)
- **Pattern:** Dependency-injected AppDbContext with automatic schema initialization and asynchronous POST webhook ingestion

## Endpoints
### POST /api/webhooks/missed-call
Ingests missed-call webhook payloads and persists lead state for downstream SMS follow-up.

**Sample Payload:**
json
{
"From": "+15551234567"
}
