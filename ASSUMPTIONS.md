# Assumptions

1. **Student ID Validation (`X-Student-Id`)**
   The brief specifies that `X-Student-Id` carries the student's 17-digit Birth Registration Number, without detailing validation rules. I require `X-Student-Id` to be a 17-digit numeric string (accepting both English `0-9` and Bangla `০-৯` digits, normalized to ASCII) and return `400 Bad Request` if missing or invalid, so malformed headers are caught early instead of creating bad records.

2. **Re-applying After Expiry or Cancellation, and Applying Across Shifts**
   Rule 3 states a student cannot keep more than one active (`HELD`) or `CONFIRMED` application in the same class at the same time. I return `409 Conflict` if the student already has an active `HELD` or `CONFIRMED` record in that class, while allowing them to re-apply to the same class if their earlier hold became `EXPIRED` or `CANCELLED` (so guardians whose timer ran out can try again if seats remain). Applying to a different shift/class is also allowed because each shift is an independent class and `/me/holds` lists multiple applications per student.

3. **Class Creation & ID Formats (`POST /classes`)**
   The brief leaves ID formats and input limits open. Class and hold IDs use short prefixed strings (`c_<8-hex>` and `h_<8-hex>`) matching the readable style in the examples. Class `name` must be non-empty (up to 256 characters) and `totalSeats` must be between `1` and `100000`, returning `400 Bad Request` otherwise. Duplicate class names are allowed so automated test runs can create classes like `"Class 6 - Morning Shift"` repeatedly without colliding.

4. **HTTP `4xx` Status Codes for Confirm and Cancel Edge Cases**
   The brief requires `4xx` (never `500`) on invalid actions, `200` on confirm, and `204` on cancel, without specifying every error status code:
   - Missing or non-17-digit `X-Student-Id` returns `400 Bad Request` (invalid client input).
   - Unknown `classId` or `holdId` returns `404 Not Found` (resource does not exist).
   - Confirming or cancelling another student's hold returns `403 Forbidden` (valid student header, but not the owner of that hold).
   - Confirming an `EXPIRED` or `CANCELLED` hold, or cancelling a `CONFIRMED`, `EXPIRED`, or `CANCELLED` hold, returns `409 Conflict` (the hold's current state does not allow the transition).
   - Calling `POST /holds/{holdId}/confirm` again on an already `CONFIRMED` hold by the same student returns `200 OK` without increasing `confirmedSeats` a second time, satisfying the idempotency rule.

5. **Automatic Hold Expiration Timing**
   To return seats automatically without requiring manual API calls, `HoldExpiryWorker` runs every 1 second in the background to mark elapsed holds as `EXPIRED`. Because a request could arrive milliseconds before the next worker tick, read and write queries also check `expires_at <= UtcNow` inline so expired holds never block seats.

6. **Browser Persistence and Live Seat Updates in UI**
   Since there is no login system, the frontend saves the 17-digit `X-Student-Id` and selected `holdId` in `localStorage` so refreshing or reopening the browser restores the student's ongoing application. Class seat counts and student holds poll every 3 seconds alongside a 1-second local countdown timer so seat availability and `EXPIRED` transitions update without reloading the page.

## Multi-Instance Concurrency (Bonus §1.6)

In-memory C# locks (`lock`, `SemaphoreSlim`) only synchronize threads inside a single process, so two backend processes running on different ports against the same PostgreSQL database could still oversell seats if they read seat counts concurrently.

To enforce `heldSeats + confirmedSeats <= totalSeats` across multiple backend processes, seat operations lock rows inside PostgreSQL transactions using `SELECT ... FOR UPDATE`:

1. `POST /classes/{classId}/holds` opens a transaction and locks the target class row (`SELECT * FROM classes WHERE id = @classId FOR UPDATE`).
2. Concurrent hold requests for the same class—whether from another thread or another backend process on a different port—block at the database level until the first transaction commits or rolls back.
3. While holding the row lock, the backend marks any elapsed holds in that class as `EXPIRED`, verifies the student does not already have an active or confirmed seat in that class, and counts current `HELD` + `CONFIRMED` rows.
4. Only when `occupiedSeats < totalSeats` is the new `HELD` row inserted and committed; otherwise the transaction rolls back and returns `409 Conflict`.
5. `POST /holds/{holdId}/confirm` and `DELETE /holds/{holdId}` similarly lock the hold row (`SELECT * FROM holds WHERE hold_id = @holdId FOR UPDATE`) so concurrent confirm/cancel requests on the same hold cannot race.
