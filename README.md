# InboundFlow.WebhookAPI

**C# (.NET 10) ASP.NET Core Minimal API** with **Entity Framework Core (SQLite)** that turns missed calls into qualified leads for service contractors: a missed call triggers an instant text, an AI assistant (Gemini) qualifies the customer over SMS, and the business owner gets a ready-to-call summary.

## How it works
1. The contractor forwards unanswered calls to their Twilio number.
2. Twilio hits `POST /api/webhooks/missed-call`. The caller hears a short "we're texting you" message, gets an intro text from the contractor's number, and the owner gets a heads-up text.
3. The customer's replies hit `POST /api/webhooks/sms`. Gemini writes each reply and extracts the problem, name, address, urgency and best time. It never quotes prices or arrival times.
4. Once it has enough (or on an emergency, a request for a human, or after 6 customer messages), the owner gets a summary text and the lead is marked `Qualified - handed off`. Later texts from that customer go straight to the owner.
5. STOP / UNSUBSCRIBE etc. are recorded and the customer is never texted again; START re-enables.
6. HELP / INFO get the business's contact line (`contactPhone` / `contactEmail` in clients.json), even from opted-out numbers. No AI, no owner ping.

Every webhook verifies `X-Twilio-Signature`; forged requests get a 401.

## Configuration
Environment variables (never put these in appsettings.json, which is committed):
- `TWILIO_AUTH_TOKEN` (required): signature verification and sending.
- `TWILIO_ACCOUNT_SID`: enables real SMS sending. **If unset, the app runs in dry-run mode and prints every text to the console instead of sending it.**
- `GEMINI_API_KEY`: enables the AI assistant. If unset, customer replies are forwarded straight to the owner.
- `GEMINI_MODEL` (optional): defaults to `gemini-flash-latest`.

Clients live in `clients.json` next to the app (gitignored). Copy `clients.example.json` and fill in one entry per contractor; numbers must be E.164 (`+15551234567`). `contactPhone` and `contactEmail` (optional, the business's public contact details) go in the HELP reply; the app warns at startup if a client has neither.

## Twilio number setup (per client)
- Voice webhook ("A call comes in"): `POST https://<host>/api/webhooks/missed-call`
- Messaging webhook ("A message comes in"): `POST https://<host>/api/webhooks/sms`
- The number must sit on an approved A2P 10DLC campaign before it can text US customers.

## Data
SQLite `inboundflow.db`: `Leads`, `Messages` (full conversation per lead), `OptOuts`. Created automatically on first run. The schema is created with `EnsureCreated`, so after a model change on an existing database, delete it (test data) or add migrations (real data).
