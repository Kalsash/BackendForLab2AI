// AssistantService.cs
using BackendForLab2AI.Models;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackendForLab2AI.Services
{
    public interface IAssistantService
    {
        Task<AssistantResponse> ProcessMessageAsync(AssistantRequest request);
        Task<ConversationState?> GetConversationStateAsync(string conversationId);
        Task<bool> DeleteConversationAsync(string conversationId);
    }

    public class AssistantService : IAssistantService
    {
        private readonly IEmbeddingService _embeddingService;
        private readonly IMovieService _movieService;
        private readonly IDeepThinkService _deepThinkService; // Добавляем поле
        private readonly HttpClient _httpClient;
        private readonly ILogger<AssistantService> _logger;
        private readonly Dictionary<string, ConversationState> _conversations;

        public AssistantService(
            IEmbeddingService embeddingService,
            IMovieService movieService,
            IDeepThinkService deepThinkService,
            IHttpClientFactory httpClientFactory,
            ILogger<AssistantService> logger)
        {
            _embeddingService = embeddingService;
            _movieService = movieService;
            _deepThinkService = deepThinkService; // Сохраняем
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;
            _conversations = new Dictionary<string, ConversationState>();
        }

        public async Task<AssistantResponse> ProcessMessageAsync(AssistantRequest request)
        {
            try
            {
                // Получаем или создаем состояние беседы
                var conversation = await GetOrCreateConversationAsync(request);

                // ОПРЕДЕЛЯЕМ ЯЗЫК ПЕРВЫМ ДЕЛОМ
                var userLanguage = await DetectLanguageAsync(request.Message);
                _logger.LogInformation("Detected user language: {Language}, current conversation language: {CurrentLanguage}",
                    userLanguage, conversation.Language);

                // ВСЕГДА обновляем язык беседы на основе нового сообщения
                if (userLanguage != conversation.Language)
                {
                    conversation.Language = userLanguage;
                    _logger.LogInformation("Switched conversation language to: {Language}", userLanguage);
                }

                // Добавляем сообщение пользователя
                conversation.Messages.Add(new Message
                {
                    Role = "user",
                    Content = request.Message + (request.UseDeepThink ? " [DEEP THINK MODE]" : "")
                });

                AssistantResponse response;

                // РАЗДЕЛЕНИЕ ЛОГИКИ: Deep Think vs обычный режим
                if (request.UseDeepThink)
                {
                    // Используем отдельный сервис для Deep Think
                    response = await _deepThinkService.ProcessDeepThinkAsync(conversation, request.Message);
                }
                else
                {
                    // Обычный режим: анализируем предпочтения и используем агентный подход
                    await AnalyzeUserPreferencesAsync(conversation, request.Message);
                    var agentPlan = await AnalyzeAndPlanAsync(conversation, request.Message);

                    // Выполняем план в зависимости от типа
                    if (agentPlan.NeedsClarification && agentPlan.ClarificationQuestions.Any())
                    {
                        // Если нужно уточнение - задаем вопросы
                        response = new AssistantResponse
                        {
                            Response = GenerateClarificationResponse(agentPlan.ClarificationQuestions, conversation.Language),
                            ConversationId = conversation.ConversationId,
                            NeedsClarification = true,
                            ClarificationQuestions = agentPlan.ClarificationQuestions,
                            UsedDeepThink = false
                        };
                    }
                    else if (agentPlan.ShouldSearch)
                    {
                        // Если нужно искать - выполняем RAG
                        var relevantMovies = await ExecuteSearchAsync(conversation, agentPlan);
                        response = await GenerateRecommendationResponseAsync(conversation, relevantMovies, agentPlan);
                    }
                    else
                    {
                        // Общий ответ
                        var defaultResponse = conversation.Language == "ru"
                            ? "Привет! Расскажите, какие фильмы вам нравятся?"
                            : "Hello! Tell me what kind of movies you like?";

                        var defaultQuestions = conversation.Language == "ru"
                            ? new List<string> { "Какие жанры предпочитаете?", "Есть любимые фильмы?" }
                            : new List<string> { "What genres do you prefer?", "Do you have any favorite movies?" };

                        response = new AssistantResponse
                        {
                            Response = defaultResponse,
                            ConversationId = conversation.ConversationId,
                            NeedsClarification = true,
                            ClarificationQuestions = defaultQuestions,
                            UsedDeepThink = false
                        };
                    }
                }

                // Сохраняем ответ ассистента
                conversation.Messages.Add(new Message
                {
                    Role = "assistant",
                    Content = response.Response + (response.UsedDeepThink ? " [DEEP THINK RESPONSE]" : "")
                });
                conversation.UpdatedAt = DateTime.UtcNow;

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing assistant message");
                return new AssistantResponse
                {
                    Response = "Извините, произошла ошибка. Пожалуйста, попробуйте еще раз.",
                    ConversationId = request.ConversationId ?? Guid.NewGuid().ToString(),
                    UsedDeepThink = request.UseDeepThink
                };
            }
        }

        private async Task<string> DetectLanguageAsync(string text)
        {
            try
            {
                _logger.LogInformation("Detecting language for text: {Text}", text);

                // Сначала проверяем кириллицу - это самый надежный способ для русского
                if (text.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я'))
                {
                    _logger.LogInformation("Cyrillic characters detected, returning Russian");
                    return "ru";
                }

                var detectionPrompt = $@"Analyze the following text and return ONLY the language code (e.g. 'en', 'ru', 'fr'):

Text: ""{text}""

YOUR RESPONSE (ONLY LANGUAGE CODE):";

                var detectedLang = await CallLlmAsync(detectionPrompt, "gemma3:1b", 0.1);
                detectedLang = detectedLang.Trim().ToLower();

                _logger.LogInformation("LLM detected language: {DetectedLang}", detectedLang);

                // Проверяем валидность кода языка
                var validLanguages = new[] { "en", "ru", "fr", "de", "es", "it", "pt", "zh", "ja", "ko" };
                var result = validLanguages.Contains(detectedLang) ? detectedLang : "en";

                _logger.LogInformation("Final detected language: {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting language");
                return "en";
            }
        }

        private async Task<string> TranslateToEnglishAsync(string text, string sourceLanguage)
        {
            if (sourceLanguage == "en") return text;

            try
            {
                _logger.LogInformation("Translating from {SourceLanguage} to English: {Text}", sourceLanguage, text);

                var translationPrompt = $@"Translate the following text to English. Return ONLY the translation without any additional comments.

Source text ({sourceLanguage}): ""{text}""

English translation:";

                var translation = await CallLlmAsync(translationPrompt, "gemma3:1b", 0.1);
                var result = translation.Trim();

                _logger.LogInformation("Translation result: {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error translating to English");
                return text;
            }
        }

        private async Task<string> PreserveMovieTitlesInTranslation(string englishResponse, string targetLanguage, List<Movie> movies)
        {
            if (targetLanguage == "en") return englishResponse;

            try
            {
                // Создаем словарь для замены названий фильмов
                var movieReplacements = new Dictionary<string, string>();
                foreach (var movie in movies.Take(10)) // Берем только первые 10 чтобы не перегружать
                {
                    movieReplacements[movie.Title] = movie.Title; // Сохраняем оригинальное название
                }

                // Заменяем названия фильмов в тексте на оригинальные перед переводом
                var textWithOriginalTitles = englishResponse;
                foreach (var replacement in movieReplacements)
                {
                    textWithOriginalTitles = textWithOriginalTitles.Replace(replacement.Key, $"MOVIE_TITLE_{replacement.Key}");
                }

                // Переводим текст с замененными названиями
                var translatedText = await TranslateFromEnglishAsync(textWithOriginalTitles, targetLanguage);

                // Возвращаем оригинальные названия фильмов
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

            try
            {
                _logger.LogInformation("Translating from English to {TargetLanguage}: {Text}", targetLanguage, text);

                var translationPrompt = $@"Translate the following text to {targetLanguage}. Return ONLY the translation without any additional comments.

English text: ""{text}""

Translation ({targetLanguage}):";

                var translation = await CallLlmAsync(translationPrompt, "gemma3:1b", 0.1);
                var result = translation.Trim();

                _logger.LogInformation("Translation result: {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error translating from English to {Language}", targetLanguage);
                return text;
            }
        }

        private async Task AnalyzeUserPreferencesAsync(ConversationState conversation, string userMessage)
        {
            try
            {
                // Переводим сообщение на английский для анализа
                var englishMessage = conversation.Language == "en"
                    ? userMessage
                    : await TranslateToEnglishAsync(userMessage, conversation.Language);

                var analysisPrompt = $@"Analyze the user's message about movies and extract preferences.
Message: {englishMessage}

Extract in JSON format ONLY the following fields (do not add other fields or text):
- genres: array of strings with genres
- moods: array of strings with moods  
- timePeriod: string or null
- language: string or null
- runtime: number or null
- likedMovies: array of strings with liked movies
- dislikedMovies: array of strings with disliked movies

IMPORTANT: Return ONLY JSON without any additional explanations, comments, or markdown formatting.

Example response:
{{""genres"": [""comedy""], ""moods"": [""funny""], ""timePeriod"": ""new"", ""language"": null, ""runtime"": null, ""likedMovies"": [""bond""], ""dislikedMovies"": []}}";

                var analysis = await CallLlmAsync(analysisPrompt, "gemma3:1b", 0.3);

                // Парсим JSON и обновляем предпочтения
                await UpdatePreferencesFromAnalysis(conversation.Preferences, analysis);

                _logger.LogInformation("Updated user preferences: {GenresCount} genres, {MoviesCount} liked movies",
                    conversation.Preferences.Genres.Count,
                    conversation.Preferences.PreviouslyLikedMovies.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing user preferences");
            }
        }

        private async Task UpdatePreferencesFromAnalysis(UserPreferences preferences, string analysisJson)
        {
            try
            {
                // Извлекаем JSON из markdown блока, если он есть
                string cleanJson = ExtractJsonFromMarkdown(analysisJson);

                using var document = JsonDocument.Parse(cleanJson);
                var root = document.RootElement;

                if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                {
                    // Добавляем новые жанры, избегая дубликатов
                    var newGenres = genres.EnumerateArray()
                        .Where(g => g.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(g.GetString()))
                        .Select(g => g.GetString() ?? "")
                        .Where(g => !preferences.Genres.Contains(g, StringComparer.OrdinalIgnoreCase));

                    preferences.Genres.AddRange(newGenres);
                }

                if (root.TryGetProperty("moods", out var moods) && moods.ValueKind == JsonValueKind.Array)
                {
                    // Добавляем новые настроения, избегая дубликатов
                    var newMoods = moods.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString()))
                        .Select(m => m.GetString() ?? "")
                        .Where(m => !preferences.Moods.Contains(m, StringComparer.OrdinalIgnoreCase));

                    preferences.Moods.AddRange(newMoods);
                }

                if (root.TryGetProperty("timePeriod", out var timePeriod) &&
                    timePeriod.ValueKind == JsonValueKind.String)
                {
                    var period = timePeriod.GetString();
                    if (!string.IsNullOrEmpty(period) && period != "null")
                        preferences.TimePeriod = period;
                }

                if (root.TryGetProperty("language", out var language) &&
                    language.ValueKind == JsonValueKind.String)
                {
                    var lang = language.GetString();
                    if (!string.IsNullOrEmpty(lang) && lang != "null")
                        preferences.LanguagePreference = lang;
                }

                if (root.TryGetProperty("runtime", out var runtime) &&
                    runtime.ValueKind == JsonValueKind.Number)
                {
                    preferences.DesiredRuntime = runtime.GetInt32();
                }

                if (root.TryGetProperty("likedMovies", out var likedMovies) &&
                    likedMovies.ValueKind == JsonValueKind.Array)
                {
                    // Добавляем новые фильмы, избегая дубликатов
                    var newMovies = likedMovies.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString()))
                        .Select(m => m.GetString() ?? "")
                        .Where(m => !preferences.PreviouslyLikedMovies.Contains(m, StringComparer.OrdinalIgnoreCase));

                    preferences.PreviouslyLikedMovies.AddRange(newMovies);
                }

                if (root.TryGetProperty("dislikedMovies", out var dislikedMovies) &&
                    dislikedMovies.ValueKind == JsonValueKind.Array)
                {
                    // Добавляем фильмы для избегания
                    var avoidedMovies = dislikedMovies.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString()))
                        .Select(m => m.GetString() ?? "")
                        .Where(m => !preferences.AvoidedMovies.Contains(m, StringComparer.OrdinalIgnoreCase));

                    preferences.AvoidedMovies.AddRange(avoidedMovies);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating preferences from analysis. Raw JSON: {AnalysisJson}", analysisJson);
            }
        }

        private async Task<AgentPlan> AnalyzeAndPlanAsync(ConversationState conversation, string userMessage)
        {
            // Переводим сообщение на английский для планирования
            var englishMessage = conversation.Language == "en"
                ? userMessage
                : await TranslateToEnglishAsync(userMessage, conversation.Language);

            var planPrompt = $@"
You are an AI assistant for movie recommendations. Analyze the user's request and create an action plan.

USER REQUEST: {englishMessage}

CONVERSATION HISTORY:
{string.Join("\n", conversation.Messages.TakeLast(3).Select(m => $"{m.Role}: {m.Content}"))}

USER PREFERENCES:
- Genres: {string.Join(", ", conversation.Preferences.Genres)}
- Moods: {string.Join(", ", conversation.Preferences.Moods)}
- Favorite movies: {string.Join(", ", conversation.Preferences.PreviouslyLikedMovies)}

ANALYZE and RETURN JSON:

1. **needs_clarification** (boolean): Whether to ask clarifying questions?
2. **clarification_questions** (array): What questions to ask (max 2)
3. **should_search** (boolean): Whether to search for movies?
4. **search_strategy** (string): How to search? Options: 
   - ""direct_query"" - by direct query
   - ""similar_to_liked"" - similar to favorites
   - ""by_genres"" - by genres
   - ""combined"" - combined
5. **search_queries** (array): What search queries to use
6. **reasoning** (string): Brief reasoning for the plan

EXAMPLE RESPONSE:
{{
    ""needs_clarification"": false,
    ""clarification_questions"": [],
    ""should_search"": true,
    ""search_strategy"": ""direct_query"",
    ""search_queries"": [""bond movie""],
    ""reasoning"": ""User explicitly requested Bond movies, can search immediately""
}}

YOUR RESPONSE (ONLY JSON):
";

            var planJson = await CallLlmAsync(planPrompt, "gemma3:1b", 0.1);
            return ParseAgentPlan(planJson);
        }

        private AgentPlan ParseAgentPlan(string planJson)
        {
            try
            {
                var cleanJson = ExtractJsonFromMarkdown(planJson);
                using var document = JsonDocument.Parse(cleanJson);
                var root = document.RootElement;

                return new AgentPlan
                {
                    NeedsClarification = root.GetProperty("needs_clarification").GetBoolean(),
                    ClarificationQuestions = root.GetProperty("clarification_questions")
                        .EnumerateArray()
                        .Select(q => q.GetString() ?? "")
                        .Where(q => !string.IsNullOrEmpty(q))
                        .ToList(),
                    ShouldSearch = root.GetProperty("should_search").GetBoolean(),
                    SearchStrategy = root.GetProperty("search_strategy").GetString() ?? "direct_query",
                    SearchQueries = root.GetProperty("search_queries")
                        .EnumerateArray()
                        .Select(q => q.GetString() ?? "")
                        .Where(q => !string.IsNullOrEmpty(q))
                        .ToList(),
                    Reasoning = root.GetProperty("reasoning").GetString() ?? ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing agent plan");
                return new AgentPlan
                {
                    NeedsClarification = true,
                    ClarificationQuestions = new List<string> { "What exactly are you looking for?" },
                    ShouldSearch = false,
                    SearchStrategy = "direct_query",
                    SearchQueries = new List<string>(),
                    Reasoning = "Analysis error, need clarification"
                };
            }
        }

        private async Task<List<Movie>> ExecuteSearchAsync(ConversationState conversation, AgentPlan plan)
        {
            var relevantMovies = new List<Movie>();

            try
            {
                // Переводим поисковые запросы на английский
                var englishSearchQueries = new List<string>();
                foreach (var query in plan.SearchQueries)
                {
                    var englishQuery = conversation.Language == "en"
                        ? query
                        : await TranslateToEnglishAsync(query, conversation.Language);
                    englishSearchQueries.Add(englishQuery);
                }

                // Используем переведенные запросы из плана агента
                var searchQueries = englishSearchQueries.Any()
                    ? englishSearchQueries
                    : await BuildFallbackSearchQueriesAsync(conversation);

                _logger.LogInformation("Executing search with strategy: {Strategy}, queries: {Queries}",
                    plan.SearchStrategy, string.Join(" | ", searchQueries));

                var allMovies = new List<Movie>();
                foreach (var query in searchQueries)
                {
                    var movies = await _embeddingService.FindSimilarMoviesAsync(
                        query, 20, "bge-m3", "cosine"); // Увеличиваем лимит

                    allMovies.AddRange(movies.Select(m => m.Movie));
                }

                // УЛУЧШЕННАЯ ФИЛЬТРАЦИЯ И ДЕДУПЛИКАЦИЯ
                relevantMovies = allMovies
                    .GroupBy(m => m.Id)
                    .Select(g => g.First()) // Убираем дубликаты по ID
                    .Where(m => IsRelevantMovie(m, conversation.Preferences, plan.SearchQueries))
                    .OrderByDescending(m => CalculateRelevanceScore(m, conversation.Preferences, plan.SearchQueries))
                    .Take(8) // Берем меньше, но более релевантных
                    .ToList();

                _logger.LogInformation("Found {Count} relevant movies using strategy: {Strategy}",
                    relevantMovies.Count, plan.SearchStrategy);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing search");
            }

            return relevantMovies;
        }

        private bool IsRelevantMovie(Movie movie, UserPreferences preferences, List<string> searchQueries)
        {
            // Базовые проверки из FilterByPreferences
            if (!FilterByPreferences(movie, preferences))
                return false;

            // Дополнительные проверки релевантности
            var title = movie.Title?.ToLower() ?? "";
            var overview = movie.Overview?.ToLower() ?? "";

            // Проверяем соответствие поисковым запросам
            foreach (var query in searchQueries)
            {
                var lowerQuery = query.ToLower();
                if (title.Contains(lowerQuery) || overview.Contains(lowerQuery))
                {
                    return true;
                }
            }

            // Проверяем соответствие предпочтениям по жанрам
            if (preferences.Genres.Any(genre =>
                movie.Genres?.Contains(genre, StringComparison.OrdinalIgnoreCase) == true))
            {
                return true;
            }

            return false;
        }

        private double CalculateRelevanceScore(Movie movie, UserPreferences preferences, List<string> searchQueries)
        {
            double score = 0;
            var title = movie.Title?.ToLower() ?? "";
            var overview = movie.Overview?.ToLower() ?? "";

            // Более высокий вес для соответствия в названии
            foreach (var query in searchQueries)
            {
                var lowerQuery = query.ToLower();
                if (title.Contains(lowerQuery)) score += 3.0;
                if (overview.Contains(lowerQuery)) score += 1.0;
            }

            // Бонус за популярность (если есть рейтинг)
            if (movie.VoteAverage > 0) score += movie.VoteAverage / 2;

            // Бонус за соответствие жанрам
            if (preferences.Genres.Any(genre =>
                movie.Genres?.Contains(genre, StringComparison.OrdinalIgnoreCase) == true))
            {
                score += 2.0;
            }

            // Бонус за более новые фильмы (если есть год выпуска)
            if (movie.ReleaseDate.HasValue && movie.ReleaseDate.Value.Year > 2000)
            {
                score += 1.0;
            }

            return score;
        }

        private async Task<List<string>> BuildFallbackSearchQueriesAsync(ConversationState conversation)
        {
            var queries = new List<string>();
            var preferences = conversation.Preferences;

            // Оригинальный запрос пользователя (переводим на английский)
            var userMessage = conversation.Messages.LastOrDefault(m => m.Role == "user")?.Content;
            if (!string.IsNullOrEmpty(userMessage))
            {
                var englishMessage = conversation.Language == "en"
                    ? userMessage
                    : await TranslateToEnglishAsync(userMessage, conversation.Language);
                queries.Add(englishMessage);
            }

            // Любимые фильмы (уже должны быть на английском из анализа)
            foreach (var likedMovie in preferences.PreviouslyLikedMovies.Take(2))
            {
                queries.Add(likedMovie);
            }

            // Жанры (уже должны быть на английском из анализа)
            if (preferences.Genres.Any())
            {
                queries.Add(string.Join(" ", preferences.Genres));
            }

            return queries.Any() ? queries : new List<string> { "popular movies" };
        }

        private string BuildEnhancedContext(ConversationState conversation, List<Movie> movies, AgentPlan plan)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== RECOMMENDATION CONTEXT ===");
            sb.AppendLine($"Search strategy: {plan.SearchStrategy}");
            sb.AppendLine($"Reasoning: {plan.Reasoning}");
            sb.AppendLine();

            sb.AppendLine("USER PREFERENCES:");
            sb.AppendLine($"- Genres: {string.Join(", ", conversation.Preferences.Genres)}");
            sb.AppendLine($"- Moods: {string.Join(", ", conversation.Preferences.Moods)}");
            sb.AppendLine($"- Favorite movies: {string.Join(", ", conversation.Preferences.PreviouslyLikedMovies)}");
            sb.AppendLine();

            sb.AppendLine("FOUND MOVIES FOR RECOMMENDATION:");
            sb.AppendLine("(recommend only from this list - USE EXACT TITLES AS SHOWN)");
            sb.AppendLine();

            foreach (var movie in movies.Take(10))
            {
                var year = movie.ReleaseDate?.Year.ToString() ?? "unknown";
                var runtime = movie.Runtime.HasValue ? $"{movie.Runtime} min" : "unknown";

                // Правильное отображение жанров
                var genres = FormatGenres(movie.Genres);

                sb.AppendLine($"🎬 {movie.Title}");
                sb.AppendLine($"   📅 {year} | ⏱️ {runtime} | 🎭 {genres}");

                if (!string.IsNullOrEmpty(movie.Overview))
                {
                    var cleanOverview = movie.Overview.Replace("\n", " ").Replace("\r", " ");
                    var shortOverview = cleanOverview.Length > 120
                        ? cleanOverview.Substring(0, 120) + "..."
                        : cleanOverview;
                    sb.AppendLine($"   📖 {shortOverview}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Метод: Форматирование жанров
        private string FormatGenres(string genresJson)
        {
            if (string.IsNullOrEmpty(genresJson))
                return "not specified";

            try
            {
                // Пробуем распарсить JSON массив жанров
                if (genresJson.TrimStart().StartsWith('['))
                {
                    var genres = JsonSerializer.Deserialize<List<Genre>>(genresJson);
                    if (genres != null && genres.Any())
                    {
                        return string.Join(", ", genres.Select(g => g.Name));
                    }
                }

                // Если не JSON, возвращаем как есть
                return genresJson;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing genres JSON: {GenresJson}", genresJson);
                return genresJson;
            }
        }

        private async Task<AssistantResponse> GenerateRecommendationResponseAsync(
            ConversationState conversation, List<Movie> relevantMovies, AgentPlan plan)
        {
            if (!relevantMovies.Any())
            {
                var noMoviesResponse = conversation.Language == "ru"
                    ? "К сожалению, по вашему запросу не найдено подходящих фильмов. Может быть, уточните критерии?"
                    : "Unfortunately, no suitable movies were found for your request. Maybe clarify the criteria?";

                var noMoviesQuestions = conversation.Language == "ru"
                    ? new List<string> { "Какие жанры вас интересуют?", "Может быть, другие фильмы вам нравятся?" }
                    : new List<string> { "What genres interest you?", "Maybe you like other movies?" };

                return new AssistantResponse
                {
                    Response = noMoviesResponse,
                    ConversationId = conversation.ConversationId,
                    NeedsClarification = true,
                    ClarificationQuestions = noMoviesQuestions,
                    UsedDeepThink = false 
                };
            }

            var context = BuildEnhancedContext(conversation, relevantMovies, plan);

            // УЛУЧШЕННЫЙ ПРОМПТ с четкими инструкциями
            var responsePrompt = $@"
{context}

CONVERSATION HISTORY:
{string.Join("\n", conversation.Messages.Skip(1).Select(m => $"{m.Role}: {m.Content}"))}

AGENT PLAN:
- Search strategy: {plan.SearchStrategy}
- Reasoning: {plan.Reasoning}

CRITICAL INSTRUCTIONS:
1. Recommend ONLY movies from the list above - DO NOT invent movies
2. Use the EXACT movie titles as shown in the list - DO NOT translate or modify them
3. Explain why each movie is suitable based on user preferences
4. Be friendly and natural
5. Choose 3-5 most relevant movies from the list
6. You can ask ONE clarifying question to improve future recommendations
7. If recommending Harry Potter movies, use exact titles like 'Harry Potter and the Philosopher's Stone'

YOUR RESPONSE:
";

            var englishResponse = await CallLlmAsync(responsePrompt, "gemma3:1b", 0.7);

            // Сохраняем оригинальные названия фильмов при переводе
            var finalResponse = conversation.Language == "en"
                ? englishResponse
                : await PreserveMovieTitlesInTranslation(englishResponse, conversation.Language, relevantMovies);

            return new AssistantResponse
            {
                Response = finalResponse,
                RecommendedMovies = relevantMovies.Take(5).ToList(),
                ConversationId = conversation.ConversationId,
                NeedsClarification = relevantMovies.Count < 3,
                ClarificationQuestions = GenerateDynamicQuestions(conversation, relevantMovies)
            };
        }

        private List<string> GenerateDynamicQuestions(ConversationState conversation, List<Movie> foundMovies)
        {
            var questions = new List<string>();
            var preferences = conversation.Preferences;

            if (conversation.Language == "ru")
            {
                if (foundMovies.Count > 8)
                    questions.Add("Какой жанр вас больше интересует?");
                if (!preferences.PreviouslyLikedMovies.Any())
                    questions.Add("Какие фильмы вам нравились раньше?");
            }
            else
            {
                if (foundMovies.Count > 8)
                    questions.Add("What genre are you most interested in?");
                if (!preferences.PreviouslyLikedMovies.Any())
                    questions.Add("What movies did you like before?");
            }

            return questions.Take(1).ToList(); // Только один вопрос
        }

        private string GenerateClarificationResponse(List<string> questions, string language)
        {
            if (!questions.Any())
                return language == "ru"
                    ? "Можете уточнить, что именно вы ищете?"
                    : "Can you clarify what exactly you are looking for?";

            var response = new StringBuilder();

            if (language == "ru")
            {
                response.AppendLine("Чтобы подобрать лучшие рекомендации, уточните:");
                foreach (var question in questions)
                {
                    response.AppendLine($"• {question}");
                }
            }
            else
            {
                response.AppendLine("To provide the best recommendations, please clarify:");
                foreach (var question in questions)
                {
                    response.AppendLine($"• {question}");
                }
            }

            return response.ToString();
        }

        private async Task<ConversationState> GetOrCreateConversationAsync(AssistantRequest request)
        {
            if (request.ResetConversation || string.IsNullOrEmpty(request.ConversationId))
            {
                var newConversation = new ConversationState();
                newConversation.Language = "en";
                _conversations[newConversation.ConversationId] = newConversation;

                var systemPrompt = await GetSystemPromptAsync();
                newConversation.Messages.Add(new Message
                {
                    Role = "system",
                    Content = systemPrompt
                });

                _logger.LogInformation("Created new conversation with ID: {ConversationId}, language: {Language}",
                    newConversation.ConversationId, newConversation.Language);

                return newConversation;
            }

            if (_conversations.TryGetValue(request.ConversationId, out var conversation))
            {
                _logger.LogInformation("Retrieved existing conversation: {ConversationId}, language: {Language}",
                    conversation.ConversationId, conversation.Language);
                return conversation;
            }

            return await GetOrCreateConversationAsync(new AssistantRequest { ResetConversation = true });
        }

        private async Task<string> GetSystemPromptAsync()
        {
            return @"You are a smart movie recommendation assistant. Your task is to understand user preferences and recommend suitable movies.

You can:
1. Analyze requests and extract preferences
2. Ask clarifying questions when information is insufficient
3. Search for movies using different strategies
4. Explain recommendations
5. Conduct natural conversation

IMPORTANT RULES:
- Always respond in the same language that the user uses
- Never translate movie titles - use them exactly as provided
- Only recommend movies from the provided list
- Be friendly, helpful and professional";
        }

        private string ExtractJsonFromMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var jsonBlockMatch = Regex.Match(text, @"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline);
            if (jsonBlockMatch.Success)
            {
                return jsonBlockMatch.Groups[1].Value;
            }

            var genericBlockMatch = Regex.Match(text, @"```\s*(\{.*?\})\s*```", RegexOptions.Singleline);
            if (genericBlockMatch.Success)
            {
                return genericBlockMatch.Groups[1].Value;
            }

            var jsonObjectMatch = Regex.Match(text, @"(\{.*\})", RegexOptions.Singleline);
            if (jsonObjectMatch.Success)
            {
                return jsonObjectMatch.Groups[1].Value;
            }

            return text;
        }

        private bool FilterByPreferences(Movie movie, UserPreferences preferences)
        {
            if (!string.IsNullOrEmpty(preferences.LanguagePreference) &&
                !string.IsNullOrEmpty(movie.OriginalLanguage) &&
                !movie.OriginalLanguage.Equals(preferences.LanguagePreference, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(preferences.TimePeriod) &&
                movie.ReleaseDate.HasValue)
            {
                var year = movie.ReleaseDate.Value.Year;
                if (preferences.TimePeriod.Contains("старые") && year > 2000) return false;
                if (preferences.TimePeriod.Contains("новые") && year < 2010) return false;
                if (preferences.TimePeriod.Contains("90") && (year < 1990 || year > 1999)) return false;
                if (preferences.TimePeriod.Contains("2000") && (year < 2000 || year > 2009)) return false;
            }

            if (preferences.DesiredRuntime.HasValue && movie.Runtime.HasValue)
            {
                var diff = Math.Abs(movie.Runtime.Value - preferences.DesiredRuntime.Value);
                if (diff > 30) return false;
            }

            return true;
        }

        private async Task<string> CallLlmAsync(string prompt, string model = "gemma3:1b", double temperature = 0.7)
        {
            try
            {
                _logger.LogInformation("=== CALLING OLLAMA ===");
                _logger.LogInformation($"Model: {model}");
                _logger.LogInformation($"Prompt length: {prompt.Length}");

                var request = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = temperature,
                        num_predict = 500
                    }
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var endpoints = new[] { "api/generate", "generate" };

                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        _logger.LogInformation($"Trying endpoint: {endpoint}");
                        var response = await _httpClient.PostAsync(endpoint, content);

                        _logger.LogInformation($"Response status: {response.StatusCode}");

                        if (response.IsSuccessStatusCode)
                        {
                            var responseJson = await response.Content.ReadAsStringAsync();
                            _logger.LogInformation($"Raw response: {responseJson}");

                            using var document = JsonDocument.Parse(responseJson);

                            if (document.RootElement.TryGetProperty("response", out var responseProperty))
                            {
                                var result = responseProperty.GetString() ?? "No response";
                                _logger.LogInformation($"LLM response: {result}");
                                return result;
                            }
                        }
                        else
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            _logger.LogWarning($"Endpoint {endpoint} failed: {response.StatusCode} - {errorContent}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Endpoint {endpoint} error: {ex.Message}");
                    }
                }

                return "Hello! I'm your movie recommendation assistant. Tell me what movies you like?";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling LLM");
                return "Sorry, an error occurred. Please try again.";
            }
        }

        public Task<ConversationState?> GetConversationStateAsync(string conversationId)
        {
            _conversations.TryGetValue(conversationId, out var conversation);
            return Task.FromResult(conversation);
        }

        public Task<bool> DeleteConversationAsync(string conversationId)
        {
            return Task.FromResult(_conversations.Remove(conversationId));
        }
    }

    // Модель для плана агента
    public class AgentPlan
    {
        public bool NeedsClarification { get; set; }
        public List<string> ClarificationQuestions { get; set; } = new();
        public bool ShouldSearch { get; set; }
        public string SearchStrategy { get; set; } = "direct_query";
        public List<string> SearchQueries { get; set; } = new();
        public string Reasoning { get; set; } = "";
    }
}

// Класс для жанров
public class Genre
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}