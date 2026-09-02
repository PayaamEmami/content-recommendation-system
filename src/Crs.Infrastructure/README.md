# Crs.Infrastructure

The infrastructure layer for CRS. Implements data access using Entity Framework Core and the repository pattern.

## Purpose

This project sits between the domain layer (`Crs.Core`) and the database. It provides:
- Database context and entity configurations
- Concrete implementations of repository interfaces
- Data access concerns isolated from business logic

## Key Design Decisions

### Repository Pattern
All database operations go through repository interfaces defined in `Crs.Core`. This provides:
- **Testability**: Easy to mock repositories in unit tests
- **Flexibility**: Can swap database implementations without changing business logic
- **Abstraction**: API layer doesn't need to know about EF Core or PostgreSQL

### Fluent API Over Data Annotations
Entity configurations use Fluent API (separate configuration classes) instead of attributes on domain models. This keeps `Crs.Core` entities clean and database-agnostic.

### Table-Per-Hierarchy (TPH) Inheritance
All content types (Paper, Video, BlogPost, etc.) are stored in a single `Content` table with a discriminator column. This simplifies queries and foreign key relationships while still maintaining type safety in code.

**Why TPH over Table-Per-Type (TPT)?**
- Better query performance (no joins needed when querying all content)
- Simpler foreign keys (recommendations/votes point to one table)
- Content types share 80%+ of their fields

**Trade-off**: Some null columns for type-specific fields (e.g., `DOI` only exists for Papers).

## Database Schema Highlights

### Indexes
Performance-critical queries are optimized with indexes:
- `User.Email` (unique) - Login lookups
- `Topic.Slug` (unique) - URL-based topic queries
- `ContentVote (UserId, ContentId)` (composite unique) - Prevents duplicate votes
- `Recommendation (UserId, Date, FeedType)` (composite) - Fast feed retrieval

### Relationships & Cascade Behavior
- **Many-to-Many**: User ↔ Topics, Content ↔ Topics (using join tables)
- **One-to-Many**: User → Votes (cascade delete), User → Recommendations (cascade delete)
- **One-to-Many**: Content → Votes (cascade delete), Content → Recommendations (restrict delete)

**Why restrict delete on Content → Recommendations?**
Recommendations are historical records. If a content is deleted, we want to preserve the fact that it was recommended (for metrics/auditing), not cascade delete the history.

### Unique Constraints
- **User.Email**: One account per email
- **Topic.Slug**: URL-friendly unique identifiers
- **ContentVote (UserId, ContentId)**: Users can't vote twice on same content

## Usage

### Register in Dependency Injection
```csharp
// In Program.cs or Startup.cs
builder.Services.AddInfrastructure(builder.Configuration);
```

This registers:
- `CrsDbContext` with PostgreSQL provider
- All repository implementations as scoped services
- Shared observability plumbing for metrics, structured logging, and OpenTelemetry/X-Ray export

### Connection String Configuration
```json
// appsettings.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=crsdb;Username=crsadmin;Password=..."
  }
}
```

### Vector store (pgvector)
```json
// appsettings.json
{
  "Embedding": {
    "ModelName": "text-embedding-3-small",
    "Dimensions": 1536,
    "MaxBatchSize": 100
  }
}
```

Embeddings are stored in Postgres table `ContentEmbeddings` (`vector(1536)`). The API applies the pgvector extension via EF migrations. Use the `pgvector/pgvector:pg15` image locally and on Lightsail.

### Migrations
```bash
# Create a new migration
dotnet ef migrations add MigrationName --startup-project ../Crs.Api

# Apply migrations to database
dotnet ef database update --startup-project ../Crs.Api
```

**Note**: Migrations require a startup project with the connection string configuration. Infrastructure project alone doesn't have an entry point.

## Important Notes

### Eager vs Lazy Loading
Repositories use explicit eager loading (`.Include()`) rather than lazy loading. This:
- Makes queries predictable and explicit
- Prevents N+1 query problems
- Makes it obvious what data is being loaded

### Async All The Way
All repository methods are async with `CancellationToken` support. This is non-negotiable for scalability.

### Connection Resilience
The DbContext is configured with `EnableRetryOnFailure()` for automatic retry on transient PostgreSQL errors (network blips, timeouts, etc.).

### Observability
`Crs.Infrastructure` owns the shared observability registration used by both the API and jobs:
- JSON console logging in non-development environments
- OpenTelemetry tracing for custom activities, outbound HTTP, and PostgreSQL via `Npgsql.OpenTelemetry`
- CloudWatch Embedded Metric Format emission through `IObservabilityMetrics`
- Common defaults for `Service`, `Environment`, and `ExecutionEnvironment`
