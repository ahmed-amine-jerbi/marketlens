# API problem types

Every error from the Marketlens API is an RFC 9457 problem details document
(`application/problem+json`) with a stable `type`. Extra members name what went wrong.

| `type` | Status | When | Extra members |
|---|---|---|---|
| `urn:marketlens:problem:invalid-parameter` | 400 | A query or path parameter is missing, malformed or out of range; also an amount whose converted value is too large to represent | `parameter` |
| `urn:marketlens:problem:unknown-asset` | 404 | A well-formed asset key Marketlens doesn't track | `asset` |
| `urn:marketlens:problem:rate-unavailable` | 503 | A price an answer needs hasn't arrived yet (provider loading or unreachable) | `asset` |
| `urn:marketlens:problem:too-many-requests` | 429 | The client's IP passed the per-minute limit; see `Retry-After` | — |

Example:

```json
{
  "type": "urn:marketlens:problem:unknown-asset",
  "title": "Marketlens doesn't track this asset.",
  "status": 404,
  "detail": "No asset 'fiat:XYZ'.",
  "asset": "fiat:XYZ"
}
```

The SignalR hub reports refusals as `HubException` messages instead (unknown key, too many assets,
too many subscribe calls). Owner: [ApiProblems](../../services/api/Marketlens.Api/Http/ApiConventions.cs).
