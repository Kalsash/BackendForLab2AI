using BackendForLab2AI.Data;
using BackendForLab2AI.Models;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BackendForLab2AI.Services
{
    public class EmbeddingService : IEmbeddingService
    {
        private readonly MovieContext _context;
        private readonly HttpClient _httpClient;
        private readonly ILogger<EmbeddingService> _logger;

        // Кэш эмбеддингов для избежания повторных вычислений
        private static readonly Dictionary<string, Dictionary<int, List<float>>> _embeddingCache = new();

        public EmbeddingService(MovieContext context, IHttpClientFactory httpClientFactory, ILogger<EmbeddingService> logger)
        {
            _context = context;
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;
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

                // КРИТИЧЕСКИ ВАЖНО - логируем сырой JSON
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


        public async Task<Dictionary<int, List<float>>> GenerateAllMovieEmbeddingsAsync(string model = "nomic-embed-text")
        {
            var cacheKey = $"{model}_all_movies";
            if (_embeddingCache.ContainsKey(cacheKey))
                return _embeddingCache[cacheKey];

            // БЕРЕМ ФИЛЬМЫ ИЗ БАЗЫ ДАННЫХ!
            var movies = await _context.Movies
                .Where(m => !string.IsNullOrEmpty(m.Overview) && m.Overview.Length > 50)
                .Take(1000) // Начнем с 1000 фильмов для теста
                .ToListAsync();

            var embeddings = new Dictionary<int, List<float>>();

            Console.WriteLine($"Computing embeddings for {movies.Count} movies...");

            foreach (var movie in movies)
            {
                try
                {
                    // Создаем текст для эмбеддинга из данных фильма
                    var text = BuildMovieText(movie);
                    var embedding = await GetEmbeddingAsync(text, model);

                    if (embedding.Any())
                    {
                        embeddings[movie.Id] = embedding;
                        Console.WriteLine($"Computed embedding for: {movie.Title}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error computing embedding for movie {movie.Id}: {ex.Message}");
                }
            }

            _embeddingCache[cacheKey] = embeddings;
            Console.WriteLine($"Computed embeddings for {embeddings.Count} movies");
            return embeddings;
        }

        public async Task<List<MovieRecommendation>> FindSimilarMoviesAsync(string query, int topK = 10,
            string model = "nomic-embed-text", string distanceMetric = "cosine")
        {
            // 1. Получаем эмбеддинг запроса пользователя
            var queryEmbedding = await GetEmbeddingAsync(query, model);

            // 2. Получаем эмбеддинги ВСЕХ фильмов из базы данных
            var movieEmbeddings = await GenerateAllMovieEmbeddingsAsync(model);

            // 3. Сравниваем запрос с каждым фильмом
            var similarities = new List<MovieRecommendation>();

            foreach (var (movieId, movieEmbedding) in movieEmbeddings)
            {
                var similarity = CalculateSimilarity(queryEmbedding, movieEmbedding, distanceMetric);
                var movie = await _context.Movies.FindAsync(movieId);

                if (movie != null)
                {
                    similarities.Add(new MovieRecommendation
                    {
                        Movie = movie,
                        SimilarityScore = similarity,
                        DistanceMetric = distanceMetric
                    });
                }
            }

            // 4. Возвращаем самые похожие
            return similarities
                .OrderByDescending(s => s.SimilarityScore)
                .Take(topK)
                .ToList();
        }

        public async Task<List<MovieRecommendation>> FindSimilarMoviesByTitleAsync(string movieTitle, int topK = 10,
            string model = "nomic-embed-text", string distanceMetric = "cosine")
        {
            var movie = await _context.Movies
                .FirstOrDefaultAsync(m => m.Title != null && m.Title.ToLower().Contains(movieTitle.ToLower()));

            if (movie == null || string.IsNullOrEmpty(movie.Overview))
                return new List<MovieRecommendation>();

            var queryText = BuildMovieText(movie);
            return await FindSimilarMoviesAsync(queryText, topK, model, distanceMetric);
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
            return 1.0 / (1.0 + distance); // Convert distance to similarity
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
}
