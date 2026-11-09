# Assumptions and Design Notes

## Assumptions

1. **Student ID Format (`X-Student-Id`)**
   The `X-Student-Id` request header represents the student's 17-digit Birth Registration Number. The backend checks that the header is present and contains 17 numeric digits (`0-9`). If it is missing or invalid, the API returns `400 Bad Request`.

2. **Re-applying After Expiry or Cancellation**
   A student can only have one active (`HELD`) or `CONFIRMED` application per class at a time. Trying to create another hold in the same class returns `409 Conflict`. If a previous hold in that class has already become `EXPIRED` or `CANCELLED`, the student is allowed to apply again if seats are available.

3. **Class Creation (`POST /classes`)**
   Class `name` must be non-empty and `totalSeats` must be a positive integer greater than zero. Invalid values return `400 Bad Request`.

4. **Confirming and Cancelling Holds**
   - Only the student who created a hold can confirm or cancel it. If the `X-Student-Id` header belongs to a different student, the API returns `403 Forbidden`.
   - `POST /holds/{holdId}/confirm` is idempotent for the hold owner: calling it multiple times on an already confirmed hold returns `200 OK` with `CONFIRMED` status and only counts the seat once.
   - Confirming or cancelling an `EXPIRED` or `CANCELLED` hold returns `409 Conflict`.

5. **Hold Expiration**
   A background worker (`HoldExpiryWorker`) runs every second to mark expired holds as `EXPIRED`. Queries and seat operations also check `expires_at <= UtcNow` directly so expired holds never block available seats between worker ticks.

## Handling Concurrency Across Multiple Backend Instances

If two backend servers run on different ports and connect to the same PostgreSQL database, in-memory locks (`lock` or `SemaphoreSlim`) only work inside a single process and will not stop race conditions between the two servers.

To keep `heldSeats + confirmedSeats <= totalSeats` across multiple backend processes, seat reservation uses PostgreSQL row-level locking (`SELECT ... FOR UPDATE`) inside a database transaction:

1. When `POST /classes/{classId}/holds` is called, the backend starts a transaction and locks the class row:
   ```sql
   SELECT * FROM classes WHERE id = @classId FOR UPDATE;
   ```
2. Any other request trying to reserve a seat in the same class (from another thread or another backend server instance) waits on PostgreSQL until this transaction finishes.
3. Inside the transaction, the server marks any elapsed holds for that class as `EXPIRED`, checks that the student does not already have an active or confirmed hold in the class, and counts current `HELD` + `CONFIRMED` rows.
4. If `occupiedSeats < totalSeats`, the new hold row is inserted and the transaction is committed. Otherwise the transaction rolls back and returns `409 Conflict`.
5. `POST /holds/{holdId}/confirm` and `DELETE /holds/{holdId}` similarly lock the target row in `holds` (`SELECT * FROM holds WHERE hold_id = @holdId FOR UPDATE`) so concurrent confirm or cancel requests are processed safely one at a time.
