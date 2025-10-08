using BackendForLab2AI.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Text.Json;

namespace BackendForLab2AI.Data
{
    public class MovieContext : DbContext
    {
        public MovieContext(DbContextOptions<MovieContext> options) : base(options)
        {
        }

        public DbSet<Movie> Movies { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Конфигурация модели Movie
            modelBuilder.Entity<Movie>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired();
                entity.Property(e => e.OriginalTitle).IsRequired();
                entity.Property(e => e.ReleaseDate).IsRequired(false);
            });
        }

        public async Task InitializeDatabaseAsync()
        {
            // Создаем базу данных, если она не существует
            await Database.EnsureCreatedAsync();

            // Проверяем, есть ли уже данные в базе
            if (!Movies.Any())
            {
                await SeedMoviesFromJsonAsync();
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

                // Используем настройки для более гибкой десериализации
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                // Десериализуем как список JsonElement для полного контроля
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
                            Console.WriteLine($"Error processing movie at index {i}: {ex.Message}");
                        }
                    }

                    if (movies.Any())
                    {
                        await Movies.AddRangeAsync(movies);
                        await SaveChangesAsync();

                        Console.WriteLine($"Successfully seeded {successCount} movies to database.");
                        if (errorCount > 0)
                        {
                            Console.WriteLine($"{errorCount} movies were skipped due to errors.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error seeding database: {ex.Message}");
                throw;
            }
        }

        private Movie? ParseMovieFromElement(JsonElement element, int index)
        {
            try
            {
                // Безопасное извлечение всех свойств
                var movie = new Movie
                {
                    Adult = SafeGetBool(element, "adult"),
                    BelongsToCollection = SafeGetString(element, "belongsToCollection"),
                    Budget = SafeGetLong(element, "budget"),
                    Genres = SafeGetString(element, "genres"),
                    Homepage = SafeGetString(element, "homepage"),
                    MovieId = SafeGetInt(element, "id") ?? index, // используем индекс как fallback ID
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
                    return value.ToString(); // для объектов и массивов
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