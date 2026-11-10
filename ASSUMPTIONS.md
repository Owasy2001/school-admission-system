# Assumptions

1. **`X-Student-Id` Validation & Bangla Digits**
   - **Assumption:** `X-Student-Id` must be a 17-digit numeric string (accepting both English `0-9` and Bangla `০-৯` digits, normalized to ASCII `0-9`). Missing or malformed headers return `400 Bad Request`.
   - **Why:** Section 1.4 specifies that `X-Student-Id` carries the student's 17-digit Birth Registration Number, and Section 1.5 requires `4xx` on invalid input. Normalizing Bangla numerals prevents unnecessary errors when guardians type with a Bangla keyboard, while strict 17-digit validation rejects malformed inputs early.

2. **One Active/Confirmed Hold Per Class & Re-applying After Expiry/Cancellation**
   - **Assumption:** Uniqueness of an active (`HELD`) or `CONFIRMED` application is enforced per `classId` (`409 Conflict` if the student already holds or confirmed a seat in that class). Once a hold becomes `EXPIRED` or `CANCELLED`, the same student may apply again if seats remain.
   - **Why:** Section 1.3 models each shift as a separate class, `POST /classes` only accepts `name` and `totalSeats` (no separate grade field), `GET /me/holds` returns an array of applications for a student, and Rule 3 states a student cannot keep more than one active or confirmed seat *in a class* at the same time. Allowing re-application after expiry/cancellation lets guardians try again if they missed the payment window.

3. **Class Creation Rules & Readable IDs (`POST /classes`)**
   - **Assumption:** Class IDs and Hold IDs use short prefixed strings (`c_<8-hex>` and `h_<8-hex>`). Class `name` must be non-empty after trimming (`1`–`256` chars) and `totalSeats` must be an integer between `1` and `100000` (`400 Bad Request` otherwise). Duplicate class names are allowed.
   - **Why:** Short prefixed IDs match the readable format shown in the brief (`c_123`, `h_789`) and are easy to inspect in UI and logs. Allowing duplicate class names ensures repeated automated test runs creating `"Class 6 - Morning Shift"` never fail due to leftover records.

4. **HTTP `4xx` Status Codes & Idempotent Confirmation**
   - **Assumption:** Invalid actions return specific `4xx` codes (never `500`):
     - `400 Bad Request`: Missing/non-17-digit `X-Student-Id`, malformed JSON, empty class name, or `totalSeats <= 0`.
     - `404 Not Found`: Unknown `classId` or `holdId`.
     - `403 Forbidden`: Attempting to confirm or cancel another student's hold.
     - `409 Conflict`: No available seats, duplicate active/confirmed application in the same class, confirming an `EXPIRED`/`CANCELLED` hold, or cancelling a `CONFIRMED`/`EXPIRED`/`CANCELLED` hold.
     - `200 OK` (Idempotent): Calling `POST /holds/{holdId}/confirm` multiple times on an already `CONFIRMED` hold owned by that student returns `200 OK` (`{ holdId, status: "CONFIRMED" }`) without increasing `confirmedSeats` again.
   - **Why:** Distinguishing `400`, `403`, `404`, and `409` follows standard REST semantics while satisfying the rule that invalid inputs return `4xx`, ownership is enforced, and repeated confirmations remain idempotent.

5. **Automatic Seat Release Without Manual API Calls**
   - **Assumption:** `HoldExpiryWorker` sweeps every `1` second in the background to mark elapsed `HELD` records as `EXPIRED`, and read/write queries also check `ExpiresAt <= DateTime.UtcNow` inline.
   - **Why:** The background worker ensures expired holds transition to `EXPIRED` in PostgreSQL even when traffic is idle, while inline `ExpiresAt` checks eliminate any sub-second timing gap if a request arrives right before the next worker tick.

6. **UI Session Persistence, Live Updates & Admin Access**
   - **Assumption:** The 17-digit `X-Student-Id` and selected `holdId` are saved in `localStorage`; seat counts and student holds poll every `3` seconds alongside a `1`-second local countdown timer; School Admin class creation is available directly in the UI without authentication.
   - **Why:** Saving `X-Student-Id` in `localStorage` restores ongoing applications after page refresh or browser reopen without a login system, and `3`-second polling keeps class seat counts fresh without overloading the API.

## Multi-Instance Concurrency (Bonus: Section 1.6)

In-memory C# synchronization (`lock` or `SemaphoreSlim`) only coordinates threads inside a single OS process. If two backend processes run simultaneously on different ports (e.g., `:8080` and `:8081`) sharing one PostgreSQL database, both processes could read `availableSeats = 1` at the same instant and insert two holds, violating `heldSeats + confirmedSeats <= totalSeats`.

To guarantee seat limits across multiple backend processes, seat mutations use **PostgreSQL row-level pessimistic locking (`SELECT ... FOR UPDATE`)** inside database transactions:

1. In `POST /classes/{classId}/holds`, `AdmissionService` begins a database transaction and locks the target class row (`FromSqlInterpolated($"SELECT * FROM classes WHERE id = {classId} FOR UPDATE")`).
2. Any concurrent request for the same `classId` (whether on another thread or another backend process on a different port) blocks at PostgreSQL until the lock holder commits or rolls back.
3. While holding the row lock, the transaction marks any elapsed holds in that class as `EXPIRED`, checks that the student does not already hold an active/confirmed seat in that class, and counts current `HELD` + `CONFIRMED` rows.
4. Only if `occupiedSeats < totalSeats` does it insert the new `HELD` row and commit; otherwise it rolls back and returns `409 Conflict`.
5. `POST /holds/{holdId}/confirm` and `DELETE /holds/{holdId}` similarly lock the target hold row (`SELECT * FROM holds WHERE hold_id = {holdId} FOR UPDATE`) so concurrent confirm/cancel calls on the same hold serialize cleanly. This is verified in `MultipleServerInstances_RespectTotalSeatLimit` (`AdmissionIntegrationTests.cs`).
