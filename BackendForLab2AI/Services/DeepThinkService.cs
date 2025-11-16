// DeepThinkService.cs
using BackendForLab2AI.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackendForLab2AI.Services
{
    public interface IDeepThinkService
    {
        Task<AssistantResponse> ProcessDeepThinkAsync(ConversationState conversation, string userMessage);
    }
    public class DeepThinkService : IDeepThinkService
    {
        private readonly IEmbeddingService _embeddingService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<DeepThinkService> _logger;

        public DeepThinkService(
            IEmbeddingService embeddingService,
            IHttpClientFactory httpClientFactory,
            ILogger<DeepThinkService> logger)
        {
            _embeddingService = embeddingService;
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;
        }

        public async Task<AssistantResponse> ProcessDeepThinkAsync(ConversationState conversation, string userMessage)
        {
            try
            {
                _logger.LogInformation("Starting Deep Think processing for conversation: {ConversationId}", conversation.ConversationId);

                // 1. Расширенный анализ предпочтений
                var deepPreferences = await AnalyzePreferencesDeepThinkAsync(conversation, userMessage);

                // 2. Многоэтапный поиск фильмов
                var allFoundMovies = await ExecuteDeepSearchAsync(conversation, userMessage, deepPreferences);

                // 3. Генерация расширенного ответа
                var response = await GenerateDeepThinkResponseAsync(conversation, allFoundMovies, userMessage, deepPreferences);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Deep Think processing");
                throw; // Пробрасываем исключение для обработки в основном сервисе
            }
        }

        private async Task<DeepThinkPreferences> AnalyzePreferencesDeepThinkAsync(ConversationState conversation, string userMessage)
        {
            var englishMessage = conversation.Language == "en"
                ? userMessage
                : await TranslateToEnglishAsync(userMessage, conversation.Language);

            var deepAnalysisPrompt = $@"Analyze the user's movie preferences in EXTREME DETAIL. Consider:

USER MESSAGE: {englishMessage}

CONVERSATION HISTORY:
{string.Join("\n", conversation.Messages.TakeLast(5).Select(m => $"{m.Role}: {m.Content}"))}

EXISTING PREFERENCES:
- Genres: {string.Join(", ", conversation.Preferences.Genres)}
- Moods: {string.Join(", ", conversation.Preferences.Moods)}
- Favorite movies: {string.Join(", ", conversation.Preferences.PreviouslyLikedMovies)}

ANALYZE DEEPLY and extract:
1. Implicit preferences (what they might like based on context)
2. Emotional state and desired movie experience
3. Cultural references and themes they might enjoy
4. Similar directors/actors they might like
5. Subgenres and niche interests

Return ONLY JSON with these fields:
- explicit_genres: array
- implicit_genres: array (inferred)
- themes: array
- moods: array
- similar_to_movies: array (movies with similar vibes)
- directors_style: array
- era_preferences: array
- cultural_influences: array

JSON:";

            var analysis = await CallLlmAsync(deepAnalysisPrompt, "llama3.1", 0.3);
            return ParseDeepPreferences(analysis);
        }

        private async Task<List<Movie>> ExecuteDeepSearchAsync(ConversationState conversation, string userMessage, DeepThinkPreferences deepPreferences)
        {
            var allMovies = new List<Movie>();

            try
            {
                var englishMessage = conversation.Language == "en"
                    ? userMessage
                    : await TranslateToEnglishAsync(userMessage, conversation.Language);

                // 1. Поиск по прямому запросу
                var directResults = await _embeddingService.FindSimilarMoviesAsync(englishMessage, 15, "bge-m3", "cosine");
                allMovies.AddRange(directResults.Select(m => m.Movie));

                // 2. Поиск по явным жанрам
                foreach (var genre in conversation.Preferences.Genres.Take(3))
                {
                    var genreResults = await _embeddingService.FindSimilarMoviesAsync(genre, 10, "bge-m3", "cosine");
                    allMovies.AddRange(genreResults.Select(m => m.Movie));
                }

                // 3. Поиск по неявным жанрам из глубокого анализа
                foreach (var implicitGenre in deepPreferences.ImplicitGenres.Take(2))
                {
                    var implicitResults = await _embeddingService.FindSimilarMoviesAsync(implicitGenre, 8, "bge-m3", "cosine");
                    allMovies.AddRange(implicitResults.Select(m => m.Movie));
                }

                // 4. Поиск по темам
                foreach (var theme in deepPreferences.Themes.Take(2))
                {
                    var themeResults = await _embeddingService.FindSimilarMoviesAsync($"{theme} theme movies", 8, "bge-m3", "cosine");
                    allMovies.AddRange(themeResults.Select(m => m.Movie));
                }

                // 5. Поиск по стилю режиссера
                foreach (var directorStyle in deepPreferences.DirectorsStyle.Take(2))
                {
                    var styleResults = await _embeddingService.FindSimilarMoviesAsync($"movies with {directorStyle} style", 6, "bge-m3", "cosine");
                    allMovies.AddRange(styleResults.Select(m => m.Movie));
                }

                // Дедупликация и фильтрация
                var relevantMovies = allMovies
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .Where(m => FilterByDeepPreferences(m, conversation.Preferences, deepPreferences))
                    .OrderByDescending(m => CalculateDeepRelevanceScore(m, conversation.Preferences, deepPreferences))
                    .Take(15) // Берем больше фильмов для глубокого анализа
                    .ToList();

                _logger.LogInformation("Deep Search found {Count} unique movies", relevantMovies.Count);
                return relevantMovies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in deep search");
                return allMovies.Take(8).ToList();
            }
        }

        private async Task<AssistantResponse> GenerateDeepThinkResponseAsync(
            ConversationState conversation, List<Movie> movies, string userMessage, DeepThinkPreferences deepPreferences)
        {
            if (!movies.Any())
            {
                var noMoviesResponse = conversation.Language == "ru"
                    ? "🤔 После глубокого анализа я не нашел идеальных совпадений. Может быть, попробуйте описать желаемый фильм по-другому?"
                    : "🤔 After deep analysis, I couldn't find perfect matches. Maybe try describing your desired movie differently?";

                return new AssistantResponse
                {
                    Response = noMoviesResponse,
                    ConversationId = conversation.ConversationId,
                    RecommendedMovies = new List<Movie>(),
                    NeedsClarification = true,
                    UsedDeepThink = true
                };
            }

            var context = BuildDeepThinkContext(conversation, movies, userMessage, deepPreferences);

            var deepThinkPrompt = $@"{context}

IMPORTANT DEEP THINK INSTRUCTIONS:
1. Provide DEEP, THOUGHTFUL analysis of why these movies match
2. Consider psychological, cultural, and emotional aspects
3. Draw connections between user's stated preferences and implicit desires
4. Suggest 3-5 MOST relevant movies with detailed reasoning
5. Mention alternative options if user wants something different
6. Be insightful, not just descriptive
7. Use EXACT movie titles from the list
8. Consider movie themes, director style, cultural impact

YOUR DEEP ANALYSIS RESPONSE:";

            var englishResponse = await CallLlmAsync(deepThinkPrompt, "llama3.1", 0.8);

            var finalResponse = conversation.Language == "en"
                ? englishResponse
                : await PreserveMovieTitlesInTranslation(englishResponse, conversation.Language, movies);

            return new AssistantResponse
            {
                Response = "🔍 **Deep Think Analysis**\n\n" + finalResponse,
                RecommendedMovies = movies.Take(5).ToList(),
                ConversationId = conversation.ConversationId,
                NeedsClarification = false,
                UsedDeepThink = true
            };
        }

        private string BuildDeepThinkContext(ConversationState conversation, List<Movie> movies, string userMessage, DeepThinkPreferences deepPreferences)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== DEEP THINK ANALYSIS CONTEXT ===");
            sb.AppendLine($"USER REQUEST: {userMessage}");
            sb.AppendLine($"CONVERSATION LANGUAGE: {conversation.Language}");
            sb.AppendLine();

            sb.AppendLine("DEEP PREFERENCES ANALYSIS:");
            sb.AppendLine($"- Explicit Genres: {string.Join(", ", deepPreferences.ExplicitGenres)}");
            sb.AppendLine($"- Implicit Genres: {string.Join(", ", deepPreferences.ImplicitGenres)}");
            sb.AppendLine($"- Themes: {string.Join(", ", deepPreferences.Themes)}");
            sb.AppendLine($"- Directors Style: {string.Join(", ", deepPreferences.DirectorsStyle)}");
            sb.AppendLine($"- Cultural Influences: {string.Join(", ", deepPreferences.CulturalInfluences)}");
            sb.AppendLine();

            sb.AppendLine("POTENTIAL MOVIE MATCHES FOR DEEP ANALYSIS:");
            sb.AppendLine("(Use EXACT titles as shown below)");
            sb.AppendLine();

            foreach (var movie in movies.Take(12))
            {
                var year = movie.ReleaseDate?.Year.ToString() ?? "unknown";
                var runtime = movie.Runtime.HasValue ? $"{movie.Runtime} min" : "unknown";
                var genres = FormatGenres(movie.Genres);

                sb.AppendLine($"🎬 {movie.Title}");
                sb.AppendLine($"   📅 {year} | ⏱️ {runtime} | 🎭 {genres}");

                if (!string.IsNullOrEmpty(movie.Overview))
                {
                    var cleanOverview = movie.Overview.Replace("\n", " ").Replace("\r", " ");
                    sb.AppendLine($"   📖 {cleanOverview}");
                }

                if (movie.VoteAverage > 0)
                {
                    sb.AppendLine($"   ⭐ Rating: {movie.VoteAverage}/10");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Вспомогательные методы
        private async Task<string> TranslateToEnglishAsync(string text, string sourceLanguage)
        {
            if (sourceLanguage == "en") return text;

            var translationPrompt = $@"Translate to English: ""{text}""";
            return await CallLlmAsync(translationPrompt, "gemma3:1b", 0.1);
        }

        private async Task<string> PreserveMovieTitlesInTranslation(string englishResponse, string targetLanguage, List<Movie> movies)
        {
            if (targetLanguage == "en") return englishResponse;

            try
            {
                var movieReplacements = new Dictionary<string, string>();
                foreach (var movie in movies.Take(10))
                {
                    movieReplacements[movie.Title] = movie.Title;
                }

                var textWithOriginalTitles = englishResponse;
                foreach (var replacement in movieReplacements)
                {
                    textWithOriginalTitles = textWithOriginalTitles.Replace(replacement.Key, $"MOVIE_TITLE_{replacement.Key}");
                }

                var translatedText = await TranslateFromEnglishAsync(textWithOriginalTitles, targetLanguage);

                foreach (var replacement in movieReplacements)
                {
                    translatedText = translatedText.Replace($"MOVIE_TITLE_{replacement.Key}", replacement.Value);
                }

                return translatedText;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preserving movie titles in translation");
                return await TranslateFromEnglishAsync(englishResponse, targetLanguage);
            }
        }

        private async Task<string> TranslateFromEnglishAsync(string text, string targetLanguage)
        {
            if (targetLanguage == "en") return text;

            var translationPrompt = $@"Translate to {targetLanguage}: ""{text}""";
            return await CallLlmAsync(translationPrompt, "gemma3:1b", 0.1);
        }

        private string FormatGenres(string genresJson)
        {
            if (string.IsNullOrEmpty(genresJson)) return "not specified";

            try
            {
                if (genresJson.TrimStart().StartsWith('['))
                {
                    var genres = JsonSerializer.Deserialize<List<Genre>>(genresJson);
                    if (genres != null && genres.Any())
                    {
                        return string.Join(", ", genres.Select(g => g.Name));
                    }
                }
                return genresJson;
            }
            catch
            {
                return genresJson;
            }
        }

        private bool FilterByDeepPreferences(Movie movie, UserPreferences preferences, DeepThinkPreferences deepPreferences)
        {
            // Базовая фильтрация по предпочтениям
            if (!string.IsNullOrEmpty(preferences.LanguagePreference) &&
                !string.IsNullOrEmpty(movie.OriginalLanguage) &&
                !movie.OriginalLanguage.Equals(preferences.LanguagePreference, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Дополнительная фильтрация по глубоким предпочтениям
            var overview = movie.Overview?.ToLower() ?? "";
            var title = movie.Title?.ToLower() ?? "";

            // Проверка соответствия темам
            foreach (var theme in deepPreferences.Themes)
            {
                if (overview.Contains(theme.ToLower()) || title.Contains(theme.ToLower()))
                    return true;
            }

            return true;
        }

        private double CalculateDeepRelevanceScore(Movie movie, UserPreferences preferences, DeepThinkPreferences deepPreferences)
        {
            double score = 0;
            var overview = movie.Overview?.ToLower() ?? "";
            var title = movie.Title?.ToLower() ?? "";

            // Бонус за рейтинг
            if (movie.VoteAverage > 0) score += movie.VoteAverage;

            // Бонус за соответствие темам
            foreach (var theme in deepPreferences.Themes)
            {
                if (overview.Contains(theme.ToLower())) score += 2.0;
                if (title.Contains(theme.ToLower())) score += 1.0;
            }

            // Бонус за соответствие стилю
            foreach (var style in deepPreferences.DirectorsStyle)
            {
                if (overview.Contains(style.ToLower())) score += 1.5;
            }

            return score;
        }

        private DeepThinkPreferences ParseDeepPreferences(string analysisJson)
        {
            try
            {
                var cleanJson = ExtractJsonFromMarkdown(analysisJson);
                using var document = JsonDocument.Parse(cleanJson);
                var root = document.RootElement;

                return new DeepThinkPreferences
                {
                    ExplicitGenres = GetStringArray(root, "explicit_genres"),
                    ImplicitGenres = GetStringArray(root, "implicit_genres"),
                    Themes = GetStringArray(root, "themes"),
                    Moods = GetStringArray(root, "moods"),
                    SimilarToMovies = GetStringArray(root, "similar_to_movies"),
                    DirectorsStyle = GetStringArray(root, "directors_style"),
                    EraPreferences = GetStringArray(root, "era_preferences"),
                    CulturalInfluences = GetStringArray(root, "cultural_influences")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing deep preferences");
                return new DeepThinkPreferences();
            }
        }

        private List<string> GetStringArray(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => i.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
            return new List<string>();
        }

        private string ExtractJsonFromMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var jsonBlockMatch = Regex.Match(text, @"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline);
            if (jsonBlockMatch.Success) return jsonBlockMatch.Groups[1].Value;

            var genericBlockMatch = Regex.Match(text, @"```\s*(\{.*?\})\s*```", RegexOptions.Singleline);
            if (genericBlockMatch.Success) return genericBlockMatch.Groups[1].Value;

            var jsonObjectMatch = Regex.Match(text, @"(\{.*\})", RegexOptions.Singleline);
            if (jsonObjectMatch.Success) return jsonObjectMatch.Groups[1].Value;

            return text;
        }

        private async Task<string> CallLlmAsync(string prompt, string model = "llama3.1:8b", double temperature = 0.7)
        {
            try
            {
                var request = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = temperature, num_predict = 800 }
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("api/generate", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    using var document = JsonDocument.Parse(responseJson);

                    if (document.RootElement.TryGetProperty("response", out var responseProperty))
                    {
                        return responseProperty.GetString() ?? "No response";
                    }
                }

                return "Error in deep analysis";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling LLM in DeepThink");
                return "Deep analysis unavailable";
            }
        }
    }

    // Модель для глубоких предпочтений
    public class DeepThinkPreferences
    {
        public List<string> ExplicitGenres { get; set; } = new();
        public List<string> ImplicitGenres { get; set; } = new();
        public List<string> Themes { get; set; } = new();
        public List<string> Moods { get; set; } = new();
        public List<string> SimilarToMovies { get; set; } = new();
        public List<string> DirectorsStyle { get; set; } = new();
        public List<string> EraPreferences { get; set; } = new();
        public List<string> CulturalInfluences { get; set; } = new();
    }
}