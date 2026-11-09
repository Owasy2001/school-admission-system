# School Admission System

Online school admission system built with .NET 9, PostgreSQL, React (Vite + TypeScript), and Docker Compose.

## Running the Project

Make sure Docker Desktop is running, then start the services from the project root:

```bash
docker compose up --build
```

To run in the background:

```bash
docker compose up -d --build
```

Once the containers are up:
- Frontend UI: http://localhost:3000
- Backend API: http://localhost:8080
- PostgreSQL: `localhost:5432` (database: `school_admission`)

Database records are stored in the `pgdata` Docker volume, so classes and seat holds persist across container restarts (`docker compose down` and `docker compose up`).

## Environment Variables

The application uses the following environment variables (configured in `docker-compose.yml`):

- `DATABASE_URL`: PostgreSQL connection string (`Host=postgres;Port=5432;Database=school_admission;Username=postgres;Password=postgres`)
- `HOLD_DURATION_SECONDS`: Seat hold duration in seconds before a hold expires automatically (default: `600`)
- `VITE_API_URL`: Backend API URL used by the frontend (default: `http://localhost:8080`)

## Running Tests

### Backend Tests

Run the xUnit integration and concurrency tests locally:

```bash
dotnet test backend/SchoolAdmission.sln
```

Or run them inside Docker without installing the .NET SDK locally:

```bash
docker run --rm -v "${PWD}/backend:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test SchoolAdmission.sln
```

### Frontend Tests

```bash
cd frontend
npm install
npm test
```
