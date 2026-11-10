# AI Usage

## AI Tools Used & My Engineering Role
- **Tools Used:** **ChatGPT, Claude, and Gemini** (free browser chat versions).
- **Where AI Helped:**
  - **ChatGPT:** Initial route boilerplate in `Program.cs`, `[JsonPropertyName]` DTOs in `Dtos.cs`, and base `Dockerfile` / `docker-compose.yml` drafts.
  - **Claude:** Initial JSX layout for the four UI panels in `App.tsx`, typed `fetch` wrapper in `client.ts`, and `MM:SS` countdown formatting (`formatRemainingTime`).
  - **Gemini:** Reviewing PostgreSQL `SELECT ... FOR UPDATE` locking behavior with EF Core transactions and brainstorming edge cases for `AdmissionIntegrationTests.cs`.
- **What I Designed, Debugged & Built Directly:** I designed the transactional seat-locking flow in `AdmissionService.cs`, implemented dual-layer hold expiry (`HoldExpiryWorker` + inline `ExpiresAt <= DateTime.UtcNow` checks), added 17-digit `X-Student-Id` validation with Bangla digit (`০-৯`) normalization, built the runtime `VITE_API_URL` Nginx `/config.js` loader, and wrote/executed the 6 backend xUnit concurrency/integration tests and 3 frontend Vitest tests in Docker.

---

## Prompting Workflow & Three Optimization Techniques

Because free web chat sessions have strict message and context limits, pasting the entire brief or full source files repeatedly wastes tokens and causes field-name drift. I used three optimization techniques tailored to this project:

### 1. Parent-Child Prompting Workflow
- **How it worked:** I started a chat with one compact **Parent Prompt** defining the fixed system contract, then sent small **Child Prompts** for one isolated method or component at a time without repeating the project background.
- **Practical Example from This Project:**
  ```text
  [Parent Prompt - Pinned Contract]
  Stack: .NET 9 Minimal API + EF Core Npgsql, React 18 TS.
  Auth header: X-Student-Id (17-digit Birth Reg No).
  Hold states: HELD | CONFIRMED | EXPIRED | CANCELLED.
  Invariants: heldSeats + confirmedSeats <= totalSeats; max 1 active/confirmed hold per student per classId.

  [Child Prompt - Single Method]
  Implement CreateHoldAsync(classId, studentIdHeader) in AdmissionService:
  - Invalid/missing 17-digit X-Student-Id -> 400; missing classId -> 404.
  - Lock class row in PostgreSQL transaction; mark elapsed HELD rows EXPIRED inline.
  - Duplicate active/confirmed hold or occupiedSeats >= totalSeats -> 409.
  - Otherwise insert HELD (HOLD_DURATION_SECONDS, default 600) -> 201 { holdId, classId, studentId, status, expiresAt }.
  ```

### 2. Context Compression & Token Optimization
- **Reducing unnecessary words & repeated instructions:** I stripped out conversational filler and never pasted whole files like `App.tsx` or `Program.cs` for a localized change. Instead, I shared only the exact method signature or DTO/interface definition needed for that step.
- **Preserving critical context with fewer tokens:** When switching between ChatGPT, Claude, and Gemini, I carried over a 6-line summary of the 8 endpoint paths, status codes (`201/200/204/400/403/404/409`), and exact camelCase JSON keys (`holdId`, `heldSeats`, `confirmedSeats`, `availableSeats`, `expiresAt`). This kept `Dtos.cs` and `client.ts` aligned using minimal tokens.

### 3. Structured Constraint-First Prompts
- **Improving efficiency:** Formatting prompts as `Input -> Validation -> DB Lock -> Output Status/JSON` checklists instead of prose prevented the models from inventing unrequested features (such as login pages or payment gateways) and mapped directly to my xUnit test assertions.

---

## Where AI Made Mistakes & How I Caught Them

1. **In-memory `SemaphoreSlim` instead of PostgreSQL row locking (ChatGPT)**
   - **Mistake:** ChatGPT initially guarded seat allocation with a C# `SemaphoreSlim` in memory.
   - **How I caught & fixed it:** While implementing the Section 1.6 bonus (two backend processes on separate ports sharing one PostgreSQL database), I realized an in-memory lock only synchronizes threads inside a single process. After cross-checking PostgreSQL row locks with Gemini, I switched to locking the class row inside a DB transaction (`FromSqlInterpolated($"SELECT * FROM classes WHERE id = {classId} FOR UPDATE")`) and verified it with 30 concurrent requests across two server instances in `MultipleServerInstances_RespectTotalSeatLimit`.

2. **JSON parse failure on `204 No Content` during hold cancellation (Claude)**
   - **Mistake:** Claude's initial `requestJson` helper called `await response.json()` on every `response.ok` response.
   - **How I caught & fixed it:** `DELETE /holds/{holdId}` returns `204 No Content` with an empty body per Section 1.4. Clicking "Cancel" in the UI cancelled the hold in PostgreSQL but triggered an error banner because `response.json()` threw a `SyntaxError` on the empty body. Inspecting the Chrome DevTools Network/Console tabs caught the bug, and I added `if (response.status === 204) return undefined` in `client.ts`.

3. **Build-time vs. runtime `VITE_API_URL` in Docker (ChatGPT)**
   - **Mistake:** The initial `frontend/Dockerfile` only read `VITE_API_URL` during `npm run build`, hardcoding the backend URL into the compiled JS bundle.
   - **How I caught & fixed it:** Testing `VITE_API_URL` overrides in `docker-compose.yml` showed the container ignored runtime changes unless rebuilt. I added a startup script in `frontend/Dockerfile` that writes the runtime `VITE_API_URL` into `/usr/share/nginx/html/config.js` (`window.__APP_CONFIG__`, served with `Cache-Control: no-store`) every time the container starts.

4. **Sub-second hold expiry gap & `X-Student-Id` input edge cases (ChatGPT & Claude)**
   - **Mistake:** Relying only on `HoldExpiryWorker` (1-second `PeriodicTimer`) left a sub-second gap where an expired hold still appeared as `HELD` before the next tick. Also, changing `X-Student-Id` in the top bar briefly showed the previous student's hold and rejected Bangla numerals (`০-৯`).
   - **How I caught & fixed it:** My `HOLD_DURATION_SECONDS=1` test (`ExpiredHold_ReleasesSeatAutomatically`) caught the timing gap, so I added inline `ExpiresAt <= DateTime.UtcNow` checks inside read/write queries alongside the background worker. Manual browser testing caught the UI issues, which I fixed by clearing hold state immediately when `X-Student-Id` changes and normalizing Bangla digits (`০-৯` to `0-9`) on both frontend and backend.
