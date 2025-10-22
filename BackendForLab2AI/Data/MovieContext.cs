using BackendForLab2AI.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Text.Json;

namespace BackendForLab2AI.Data
{
    public class MovieContext : DbContext
    {
        private readonly ILogger<MovieContext> _logger;

        public MovieContext(DbContextOptions<MovieContext> options, ILogger<MovieContext> logger)
            : base(options)
        {
            _logger = logger;
        }


        public DbSet<Movie> Movies { get; set; }


        public async Task CreateVectorIndexAsync()
        {
            try
            {
                // Удаляем старый индекс если существует
                await Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_Movies_Embedding_Vector""");

                // Создаем новый HNSW индекс БЕЗ ef_search
                await Database.ExecuteSqlRawAsync(@"
            CREATE INDEX ""IX_Movies_Embedding_Vector"" 
            ON ""Movies"" 
            USING hnsw (""Embedding"" vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);
        ");

                _logger.LogInformation("Vector index created successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create vector index");
                throw;
            }
        }

        public async Task CreateAllVectorIndexesAsync()
        {
            try
            {
                // Удаляем старые индексы если существуют
                await Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_Movies_Embedding_Vector""");
                await Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_Movies_EmbeddingAllMiniLM_Vector""");
                await Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_Movies_EmbeddingBgeM3_Vector""");

                // Создаем новые HNSW индексы для каждого типа эмбеддингов
                await Database.ExecuteSqlRawAsync(@"
            CREATE INDEX ""IX_Movies_Embedding_Vector"" 
            ON ""Movies"" 
            USING hnsw (""Embedding"" vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);");

                await Database.ExecuteSqlRawAsync(@"
            CREATE INDEX ""IX_Movies_EmbeddingAllMiniLM_Vector"" 
            ON ""Movies"" 
            USING hnsw (""EmbeddingAllMiniLM"" vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);");

                await Database.ExecuteSqlRawAsync(@"
            CREATE INDEX ""IX_Movies_EmbeddingBgeM3_Vector"" 
            ON ""Movies"" 
            USING hnsw (""EmbeddingBgeM3"" vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);");

                _logger.LogInformation("All vector indexes created successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create vector indexes");
                throw;
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Movie>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired();
                entity.Property(e => e.OriginalTitle).IsRequired();
                entity.Property(e => e.ReleaseDate).IsRequired(false);


                // Конфигурация полей эмбеддингов
                entity.Property(e => e.Embedding)
                    .HasColumnType("vector(768)");

                entity.Property(e => e.EmbeddingAllMiniLM)
                    .HasColumnType("vector(384)"); // all-minilm обычно 384-мерный

                entity.Property(e => e.EmbeddingBgeM3)
                    .HasColumnType("vector(1024)"); // bge-m3 обычно 1024-мерный
            });
        }

        public async Task InitializeDatabaseAsync()
        {
            try
            {
                // Создаем расширение vector
                await Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;");
                _logger.LogInformation("pgvector extension installed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not install pgvector extension");
            }

            await Database.EnsureCreatedAsync();

            await Database.ExecuteSqlRawAsync(@"ALTER SEQUENCE ""Movies_Id_seq"" RESTART WITH 1;");

            if (!Movies.Any())
            {
                _logger.LogInformation("Starting Seed");
                await SeedMoviesFromJsonAsync();

                //await CreateVectorIndexAsync();
                await CreateAllVectorIndexesAsync();
            }

          
        }

        private async Task SeedMoviesFromJsonAsync()
        {
            try
            {
                var jsonFilePath = Path.Combine(Directory.GetCurrentDirectory(), "MoviesData.json");

                if (!File.Exists(jsonFilePath))
                {
                    throw new FileNotFoundException($"JSON file not found: {jsonFilePath}");
                }

                var jsonData = await File.ReadAllTextAsync(jsonFilePath);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var jsonElements = JsonSerializer.Deserialize<List<JsonElement>>(jsonData, options);

                if (jsonElements != null && jsonElements.Any())
                {
                    var movies = new List<Movie>();
                    var errorCount = 0;
                    var successCount = 0;

                    for (int i = 0; i < jsonElements.Count; i++)
                    {
                        try
                        {
                            var jsonElement = jsonElements[i];
                            var movie = ParseMovieFromElement(jsonElement, i);

                            if (movie != null)
                            {
                                movies.Add(movie);
                                successCount++;
                            }
                            else
                            {
                                errorCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            _logger.LogInformation($"Error processing movie at index {i}: {ex.Message}");
                        }
                    }

                    if (movies.Any())
                    {
                        await Movies.AddRangeAsync(movies);
                        await SaveChangesAsync();

                        _logger.LogInformation($"Successfully seeded {successCount} movies to database.");
                        if (errorCount > 0)
                        {
                            _logger.LogInformation($"{errorCount} movies were skipped due to errors.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Error seeding database: {ex.Message}");
                throw;
            }
        }

        private Movie? ParseMovieFromElement(JsonElement element, int index)
        {
            try
            {
                var movie = new Movie
                {
                    Adult = SafeGetBool(element, "adult"),
                    BelongsToCollection = SafeGetString(element, "belongsToCollection"),
                    Budget = SafeGetLong(element, "budget"),
                    Genres = SafeGetString(element, "genres"),
                    Homepage = SafeGetString(element, "homepage"),
                    MovieId = SafeGetInt(element, "id") ?? index, 
                    ImdbId = SafeGetString(element, "imdbId"),
                    OriginalLanguage = SafeGetString(element, "originalLanguage"),
                    OriginalTitle = SafeGetString(element, "originalTitle") ?? "Unknown",
                    Overview = SafeGetString(element, "overview"),
                    Popularity = SafeGetDouble(element, "popularity") ?? 0,
                    PosterPath = SafeGetString(element, "posterPath"),
                    ProductionCompanies = SafeGetString(element, "productionCompanies"),
                    ProductionCountries = SafeGetString(element, "productionCountries"),
                    ReleaseDate = SafeGetDateTime(element, "releaseDate"),
                    Revenue = SafeGetLong(element, "revenue"),
                    Runtime = SafeGetInt(element, "runtime"),
                    SpokenLanguages = SafeGetString(element, "spokenLanguages"),
                    Status = SafeGetString(element, "status"),
                    Tagline = SafeGetString(element, "tagline"),
                    Title = SafeGetString(element, "title") ?? "Unknown Title",
                    Video = SafeGetBool(element, "video"),
                    VoteAverage = SafeGetDouble(element, "voteAverage") ?? 0,
                    VoteCount = SafeGetInt(element, "voteCount") ?? 0
                };

                return movie;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing movie at index {index}: {ex.Message}");
                return null;
            }
        }

        private static bool SafeGetBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return false;

            try
            {
                if (value.ValueKind == JsonValueKind.String)
                    return bool.TryParse(value.GetString(), out bool result) && result;
                else if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    return value.GetBoolean();
                else if (value.ValueKind == JsonValueKind.Number)
                    return value.TryGetInt32(out int num) && num != 0;
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static DateTime? SafeGetDateTime(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;

            try
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var dateString = value.GetString();
                    if (string.IsNullOrEmpty(dateString)) return null;
                    return DateTime.TryParse(dateString, out DateTime date) ? date : null;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string? SafeGetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;

            try
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();
                else if (value.ValueKind == JsonValueKind.Number)
                    return value.TryGetInt64(out long num) ? num.ToString() : value.ToString();
                else if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    return value.GetBoolean().ToString();
                else
                    return value.ToString(); 
            }
            catch
            {
                return null;
            }
        }

        private static long SafeGetLong(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return 0;

            try
            {
                if (value.ValueKind == JsonValueKind.Number)
                    return value.TryGetInt64(out long result) ? result : 0;
                else if (value.ValueKind == JsonValueKind.String)
                    return long.TryParse(value.GetString(), out long result) ? result : 0;
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        private static int? SafeGetInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;

            try
            {
                if (value.ValueKind == JsonValueKind.Number)
                    return value.TryGetInt32(out int result) ? result : null;
                else if (value.ValueKind == JsonValueKind.String)
                    return int.TryParse(value.GetString(), out int result) ? result : null;
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static double? SafeGetDouble(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;

            try
            {
                if (value.ValueKind == JsonValueKind.Number)
                    return value.TryGetDouble(out double result) ? result : null;
                else if (value.ValueKind == JsonValueKind.String)
                    return double.TryParse(value.GetString(), out double result) ? result : null;
            }
            catch
            {
                return null;
            }

            return null;
        }
    }
}