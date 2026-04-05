# Crs.Api

The REST API layer for CRS. Provides HTTP endpoints for authentication, user management, content, and recommendations.

## Purpose

This project exposes the application's functionality over HTTP using ASP.NET Core Web API. It handles:
- JWT authentication and authorization
- Request/response serialization
- Input validation
- API versioning
- Error handling

## Key Design Decisions

### DTOs Over Direct Entity Exposure
All endpoints use request/response DTOs. Domain entities are never directly exposed. This:
- Prevents over-posting attacks
- Decouples API contract from domain model
- Allows independent evolution of API and domain

### Controller-Based Architecture
Traditional controllers (not minimal APIs) for better organization in a multi-endpoint API.

### JWT Authentication
Token-based authentication with access tokens (60 min) and refresh tokens (7 days). Refresh tokens stored in-memory for simplicity (production should use Redis/database).

**Trade-off**: In-memory storage means refresh tokens are lost on app restart.

### API Versioning
URL-based versioning (`/api/v1/...`) for easy discovery. Version is required in all routes.

### Problem Details (RFC 7807)
Standardized error responses. All exceptions are converted to Problem Details format by `ExceptionHandlingMiddleware`.

### Pagination
Content API supports pagination with query parameters:
- `pageNumber` - Page number (default: 1)
- `pageSize` - Items per page (default: 20, max: 100)
- `type` - Filter by content type (Paper, Video, BlogPost)
- `topicIds` - Comma-separated topic IDs for filtering

### Service Layer
Business logic lives in service classes (`AuthService`, `UserService`), not controllers. Controllers handle HTTP concerns only.

## Project Structure

```
Controllers/     # HTTP endpoints (thin, orchestration only)
DTOs/           # Request/response models
Services/       # Business logic
Extensions/     # DI configuration, claims parsing
Middleware/     # Exception handling
Configuration/  # Settings classes (JWT, etc.)
```

## Configuration

Create `appsettings.Development.local.json` with your JWT secret:

```json
{
  "JwtSettings": {
    "SecretKey": "your-secure-random-32-character-minimum-string"
  }
}
```

Then run:
```bash
cd src/Crs.Api
dotnet run
```

API starts at `https://localhost:7000`. OpenAPI spec available at `/openapi/v1.json`.

### OpenSearch (Local)

To run the API against local OpenSearch:
- Ensure Docker Desktop + WSL2 is running.
- Start the container: `docker compose up opensearch`
- Set `OpenSearch:Mode` to `Local` and `OpenSearch:Endpoint` to `http://localhost:9200`

## API Endpoints

### Authentication
- `POST /api/v1/auth/register` - Register new user
- `POST /api/v1/auth/login` - Login (returns JWT)
- `POST /api/v1/auth/refresh` - Refresh access token

### Users (requires auth)
- `GET /api/v1/users/me` - Get current user profile
- `PATCH /api/v1/users/me` - Update current user profile
- `GET /api/v1/users/me/votes` - Get the current user's vote records
- `GET /api/v1/users/me/vote-history` - Get the current user's vote history joined with content details
- `GET /api/v1/users/me/topics` - Get user's interested topics
- `PUT /api/v1/users/me/topics` - Update user's interested topics

### Topics (requires auth)
- `GET /api/v1/topics` - List all topics
- `GET /api/v1/topics/{id}` - Get topic by ID

### Content (requires auth)
- `GET /api/v1/content` - List content (paginated, filterable by type/topics)
- `GET /api/v1/content/{id}` - Get content by ID
- `POST /api/v1/content` - Create content
- `PUT /api/v1/content/{id}` - Update content
- `DELETE /api/v1/content/{id}` - Delete content
- `POST /api/v1/content/{id}/vote` - Vote on content (upvote/downvote)
- `DELETE /api/v1/content/{id}/vote` - Remove vote
- `GET /api/v1/content/{id}/vote` - Get user's vote on content

### Recommendations (requires auth)
- `GET /api/v1/recommendations` - Get today's recommendations (all feeds)
- `GET /api/v1/recommendations/{feedType}` - Get recommendations for specific feed

### Health
- `GET /health` - Health check (includes DB connectivity)

## Testing

Use `Crs.Api.http` for manual testing. Example:

```http
### Register
POST https://localhost:7000/api/v1/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123",
  "displayName": "Test User"
}

### Use returned token
GET https://localhost:7000/api/v1/users/me
Authorization: Bearer {{accessToken}}
```

## Security

See [SECURITY.md](SECURITY.md) for details on:
- Password hashing (PBKDF2 with HMAC-SHA256)
- Rate limiting (per-IP, per-endpoint)

## Known Limitations

1. **In-Memory Refresh Tokens**: Lost on restart (use Redis/database in production)
2. **No Integration Tests**: Planned for future

## Dependencies

**Microsoft Packages:**
- `Microsoft.AspNetCore.OpenApi` - Built-in OpenAPI documentation
- `Microsoft.AspNetCore.Authentication.JwtBearer` - JWT authentication
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore` - Password hashing
- `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` - Health checks
- `Microsoft.EntityFrameworkCore.Design` - EF Core tooling

**Third-Party:**
- `Asp.Versioning.Mvc` - API versioning (v8.1.0)

**Built-In Features:**
- Rate limiting (`.NET 7+` built-in middleware)

**Project References:**
- `Crs.Core` - Domain layer
- `Crs.Infrastructure` - Data access
