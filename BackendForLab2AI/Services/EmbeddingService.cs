// EmbeddingService.cs
using BackendForLab2AI.Data;
using BackendForLab2AI.Models;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace BackendForLab2AI.Services
{
    public class EmbeddingService : IEmbeddingService
    {
        private readonly MovieContext _context;
        private readonly HttpClient _httpClient;
        private readonly ILogger<EmbeddingService> _logger;
        private readonly string _embeddingsCachePath;

        // Кэш эмбеддингов для избежания повторных вычислений
        private static readonly Dictionary<string, Dictionary<int, List<float>>> _embeddingCache = new();

        public EmbeddingService(MovieContext context, IHttpClientFactory httpClientFactory, ILogger<EmbeddingService> logger)
        {
            _context = context;
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;

            _embeddingsCachePath = Path.Combine(Directory.GetCurrentDirectory(), "EmbeddingsCache");
            if (!Directory.Exists(_embeddingsCachePath))
            {
                Directory.CreateDirectory(_embeddingsCachePath);
                _logger.LogInformation("Created embeddings cache directory: {Path}", _embeddingsCachePath);
            }
        }

        public async Task<List<float>> GetEmbeddingAsync(string text, string model = "nomic-embed-text")
        {
            try
            {
                _logger.LogInformation("=== EMBEDDING REQUEST: {Model} ===", model);
                _logger.LogInformation("Text: {Text}", text);

                var request = new EmbeddingRequest
                {
                    Model = model,
                    Prompt = text
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending request to Ollama...");

                var response = await _httpClient.PostAsync("api/embeddings", content);

                _logger.LogInformation("Response status: {StatusCode}", response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("ERROR: {Error}", error);
                    return new List<float>();
                }

                var responseJson = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("RAW JSON RESPONSE: {ResponseJson}", responseJson);

                var embeddingResponse = JsonSerializer.Deserialize<EmbeddingResponse>(responseJson);

                _logger.LogInformation("Embedding dimension: {Dimension}",
                    embeddingResponse?.Embedding?.Count ?? 0);

                return embeddingResponse?.Embedding ?? new List<float>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EXCEPTION in GetEmbeddingAsync");
                return new List<float>();
            }
        }

        // EmbeddingService.cs
        public async Task<Dictionary<int, Vector>> GenerateAllMovieEmbeddingsAsync(string model = "nomic-embed-text")
        {
            try
            {
                // 1. Сначала пробуем загрузить из кэша
                var cachedEmbeddings = await LoadEmbeddingsFromFileAsync(model);
                if (cachedEmbeddings.Any())
                {
                    _logger.LogInformation("Loaded {Count} embeddings from cache for model {Model}",
                        cachedEmbeddings.Count, model);

                    // Получаем ID фильмов из кэша
                    var cachedMovieIds = cachedEmbeddings.Keys.ToList();

                    // Загружаем фильмы которые есть в кэше но не имеют эмбеддингов в БД
                    var moviesToUpdate = await _context.Movies
                        .Where(m => cachedMovieIds.Contains(m.Id) && m.Embedding == null)
                        .ToListAsync();

                    var vectorDict = new Dictionary<int, Vector>();

                    foreach (var movie in moviesToUpdate)
                    {
                        if (cachedEmbeddings.TryGetValue(movie.Id, out var embeddingList) && embeddingList.Any())
                        {
                            movie.Embedding = new Vector(embeddingList.ToArray());
                            vectorDict[movie.Id] = movie.Embedding;
                        }
                    }

                    if (moviesToUpdate.Any())
                    {
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Restored {Count} embeddings from cache to database",
                            moviesToUpdate.Count);
                    }

                    return vectorDict;
                }

                // 2. Если кэша нет - генерируем новые
                var movies = await _context.Movies
                    .Where(m => m.Embedding == null &&
                               !string.IsNullOrEmpty(m.Overview) &&
                               m.Overview.Length > 50)
                    .Take(10000)
                    .ToListAsync();

                _logger.LogInformation("Generating embeddings for {Count} movies", movies.Count);

                var embeddingsDict = new Dictionary<int, Vector>();
                var floatEmbeddingsDict = new Dictionary<int, List<float>>(); // Для кэша

                foreach (var movie in movies)
                {
                    try
                    {
                        var text = BuildMovieText(movie);
                        var embedding = await GetEmbeddingAsync(text, model);

                        if (embedding.Any())
                        {
                            // Сохраняем в БД как Vector
                            var vector = new Vector(embedding.ToArray());
                            movie.Embedding = vector;
                            embeddingsDict[movie.Id] = vector;

                            // Сохраняем для кэша как List<float>
                            floatEmbeddingsDict[movie.Id] = embedding;

                            _logger.LogInformation("Generated embedding for: {Title}", movie.Title);
                        }

                        // Сохраняем каждые 50 фильмов (для производительности)
                        if (movies.IndexOf(movie) % 50 == 0)
                        {
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Saved batch of embeddings to database");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error generating embedding for movie {MovieId}", movie.Id);
                    }
                }

                // Финальное сохранение
                await _context.SaveChangesAsync();

                // 3. Сохраняем в кэш
                if (floatEmbeddingsDict.Any())
                {
                    await SaveEmbeddingsToFileAsync(model, floatEmbeddingsDict);
                    _logger.LogInformation("Saved {Count} embeddings to cache", floatEmbeddingsDict.Count);
                }

                _logger.LogInformation("Successfully generated embeddings for {Count} movies", embeddingsDict.Count);
                return embeddingsDict;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating all movie embeddings");
                return new Dictionary<int, Vector>();
            }
        }

        public async Task<bool> SaveEmbeddingsToFileAsync(string model, Dictionary<int, List<float>> embeddings)
        {
            try
            {
                var fileName = GetEmbeddingsFileName(model);
                var options = new JsonSerializerOptions { WriteIndented = true };

                var cacheData = new EmbeddingsCache
                {
                    Model = model,
                    CreatedAt = DateTime.UtcNow,
                    MovieCount = embeddings.Count,
                    Embeddings = embeddings
                };

                var json = JsonSerializer.Serialize(cacheData, options);
                await File.WriteAllTextAsync(fileName, json);

                _logger.LogInformation("Saved {Count} embeddings to {FileName}", embeddings.Count, fileName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving embeddings to file for model {Model}", model);
                return false;
            }
        }

        public async Task<Dictionary<int, List<float>>> LoadEmbeddingsFromFileAsync(string model)
        {
            try
            {
                var fileName = GetEmbeddingsFileName(model);

                if (!File.Exists(fileName))
                {
                    _logger.LogInformation("Embeddings file not found: {FileName}", fileName);
                    return new Dictionary<int, List<float>>();
                }

                var json = await File.ReadAllTextAsync(fileName);
                var cacheData = JsonSerializer.Deserialize<EmbeddingsCache>(json);

                if (cacheData == null || cacheData.Embeddings == null)
                {
                    _logger.LogWarning("Invalid embeddings file format: {FileName}", fileName);
                    return new Dictionary<int, List<float>>();
                }

                _logger.LogInformation("Loaded {Count} embeddings from {FileName} (created: {CreatedAt})",
                    cacheData.MovieCount, fileName, cacheData.CreatedAt);

                return cacheData.Embeddings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading embeddings from file for model {Model}", model);
                return new Dictionary<int, List<float>>();
            }
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            var models = new List<string>();

            if (!Directory.Exists(_embeddingsCachePath))
                return models;

            var files = Directory.GetFiles(_embeddingsCachePath, "embeddings_*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var cacheData = JsonSerializer.Deserialize<EmbeddingsCache>(json);

                    if (cacheData != null && !string.IsNullOrEmpty(cacheData.Model))
                    {
                        models.Add(cacheData.Model);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading embeddings file: {File}", file);
                }
            }

            return models.Distinct().ToList();
        }

        public async Task<bool> DeleteEmbeddingsCacheAsync(string model = null)
        {
            try
            {
                if (model == null)
                {
                    var files = Directory.GetFiles(_embeddingsCachePath, "embeddings_*.json");
                    foreach (var file in files)
                    {
                        File.Delete(file);
                    }

                    _embeddingCache.Clear();

                    _logger.LogInformation("Deleted all embedding caches");
                    return true;
                }
                else
                {
                    var fileName = GetEmbeddingsFileName(model);
                    if (File.Exists(fileName))
                    {
                        File.Delete(fileName);
                        var cacheKey = $"{model}_all_movies";
                        _embeddingCache.Remove(cacheKey);

                        _logger.LogInformation("Deleted embeddings cache for model {Model}", model);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting embeddings cache for model {Model}", model);
                return false;
            }
        }

        public async Task<List<MovieRecommendation>> FindSimilarMoviesAsync(string query, int topK = 10,
            string model = "nomic-embed-text", string distanceMetric = "cosine")
        {
            try
            {
                var moviesWithEmbeddings = await _context.Movies.CountAsync(m => m.Embedding != null);

                //if (moviesWithEmbeddings <10000)
                //{
                //    _logger.LogInformation("No embeddings found. Generating embeddings for movies...");
                //    await GenerateAllMovieEmbeddingsAsync(model);
                //}
                await GenerateAllMovieEmbeddingsAsync(model);
                // 1. Генерируем эмбеддинг для запроса
                var queryEmbedding = await GetEmbeddingAsync(query, model);
                if (!queryEmbedding.Any())
                    return new List<MovieRecommendation>();

                // 2. Конвертируем в тип Vector
                var vector = new Vector(queryEmbedding.ToArray());

                // 3. Выполняем запрос
                var sql = @"
            SELECT m.* 
            FROM ""Movies"" m
            WHERE m.""Embedding"" IS NOT NULL 
            ORDER BY m.""Embedding"" <=> {0} 
            LIMIT {1}";

                var similarMovies = await _context.Movies
                    .FromSqlRaw(sql, vector, topK)
                    .ToListAsync();

                // 4. Вычисляем схожесть для каждого фильма
                var recommendations = new List<MovieRecommendation>();

                foreach (var movie in similarMovies)
                {
                    if (movie.Embedding != null)
                    {
                        // Конвертируем Vector обратно в List<float> для расчета схожести
                        var movieEmbeddingList = movie.Embedding.ToArray().ToList();
                        var similarity = CalculateSimilarity(queryEmbedding, movieEmbeddingList, distanceMetric);

                        recommendations.Add(new MovieRecommendation
                        {
                            Movie = movie,
                            SimilarityScore = similarity,
                            DistanceMetric = distanceMetric
                        });
                    }
                }

                return recommendations
                    .OrderByDescending(r => r.SimilarityScore)
                    .Take(topK)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding similar movies using vector DB");
                return new List<MovieRecommendation>();
            }
        }


        public async Task<List<MovieRecommendation>> FindSimilarMoviesByTitleAsync(string movieTitle, int topK = 10,
          string model = "nomic-embed-text", string distanceMetric = "cosine")
        {
            try
            {
                // 1. Находим фильм по названию
                var movie = await _context.Movies
                    .FirstOrDefaultAsync(m => m.Title != null &&
                                           m.Title.ToLower().Contains(movieTitle.ToLower()));

                if (movie == null)
                    return new List<MovieRecommendation>();

                // 2. Если у фильма есть эмбеддинг в БД - используем его для поиска
                if (movie.Embedding != null && movie.Embedding.ToArray().Length > 0)
                {
                    var embeddingString = "[" + string.Join(",", movie.Embedding) + "]";

                    return await _context.Movies
                        .FromSqlRaw(@"
                            SELECT *, ""Embedding"" <=> {0} as distance 
                            FROM ""Movies"" 
                            WHERE Id != {1} AND ""Embedding"" IS NOT NULL 
                            ORDER BY distance 
                            LIMIT {2}",
                            embeddingString, movie.Id, topK)
                        .Select(m => new MovieRecommendation
                        {
                            Movie = m,
                            SimilarityScore = 1.0 - (double)EF.Property<float>(m, "distance"),
                            DistanceMetric = distanceMetric,
                        })
                        .ToListAsync();
                }
                else
                {
                    // 3. Если эмбеддинга нет - генерируем из описания фильма
                    var queryText = BuildMovieText(movie);
                    return await FindSimilarMoviesAsync(queryText, topK, model, distanceMetric);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding similar movies by title using vector DB");
                return new List<MovieRecommendation>();
            }
        }

        public async Task<List<MovieRecommendation>> CompareEmbeddingModelsAsync(string query)
        {
            var models = new[] { "nomic-embed-text", "all-minilm", "bge-m3" };
            var allResults = new List<MovieRecommendation>();

            foreach (var model in models)
            {
                var results = await FindSimilarMoviesAsync(query, 5, model, "cosine");
                allResults.AddRange(results);
            }

            return allResults
                .OrderByDescending(r => r.SimilarityScore)
                .Take(10)
                .ToList();
        }

        public async Task<List<MovieRecommendation>> CompareDistanceMetricsAsync(string query, string model = "nomic-embed-text")
        {
            var metrics = new[] { "cosine", "euclidean", "manhattan", "dot" };
            var allResults = new List<MovieRecommendation>();

            foreach (var metric in metrics)
            {
                var results = await FindSimilarMoviesAsync(query, 5, model, metric);
                allResults.AddRange(results);
            }

            return allResults
                .OrderByDescending(r => r.SimilarityScore)
                .Take(10)
                .ToList();
        }

        private string BuildMovieText(Movie movie)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Title: {movie.Title}");
            sb.AppendLine($"Overview: {movie.Overview}");

            if (!string.IsNullOrEmpty(movie.Genres))
                sb.AppendLine($"Genres: {movie.Genres}");

            if (!string.IsNullOrEmpty(movie.OriginalLanguage))
                sb.AppendLine($"Language: {movie.OriginalLanguage}");

            if (movie.ReleaseDate.HasValue)
                sb.AppendLine($"Release Date: {movie.ReleaseDate.Value.Year}");

            return sb.ToString();
        }

        private string GetEmbeddingsFileName(string model)
        {
            var safeModelName = model.Replace("-", "_").Replace(" ", "_");
            return Path.Combine(_embeddingsCachePath, $"embeddings_{safeModelName}.json");
        }

        private double CalculateSimilarity(List<float> vec1, List<float> vec2, string metric)
        {
            if (vec1.Count != vec2.Count)
                throw new ArgumentException("Vectors must have same dimension");

            return metric.ToLower() switch
            {
                "cosine" => CosineSimilarity(vec1, vec2),
                "euclidean" => EuclideanSimilarity(vec1, vec2),
                "manhattan" => ManhattanSimilarity(vec1, vec2),
                "dot" => DotProductSimilarity(vec1, vec2),
                _ => CosineSimilarity(vec1, vec2)
            };
        }

        private double CosineSimilarity(List<float> vec1, List<float> vec2)
        {
            var dot = vec1.Zip(vec2, (a, b) => a * b).Sum();
            var norm1 = Math.Sqrt(vec1.Sum(x => x * x));
            var norm2 = Math.Sqrt(vec2.Sum(x => x * x));
            return dot / (norm1 * norm2);
        }

        private double EuclideanSimilarity(List<float> vec1, List<float> vec2)
        {
            var distance = Math.Sqrt(vec1.Zip(vec2, (a, b) => Math.Pow(a - b, 2)).Sum());
            return 1.0 / (1.0 + distance);
        }

        private double ManhattanSimilarity(List<float> vec1, List<float> vec2)
        {
            var distance = vec1.Zip(vec2, (a, b) => Math.Abs(a - b)).Sum();
            return 1.0 / (1.0 + distance);
        }

        private double DotProductSimilarity(List<float> vec1, List<float> vec2)
        {
            return vec1.Zip(vec2, (a, b) => a * b).Sum();
        }
    }

    public class EmbeddingsCache
    {
        public string Model { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int MovieCount { get; set; }
        public Dictionary<int, List<float>> Embeddings { get; set; } = new();
    }
}