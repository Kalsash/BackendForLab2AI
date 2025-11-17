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
        Task<AssistantResponse> ProcessMessageAsync(AssistantRequest request, ConversationState conversation);
        Task<ConversationState?> GetConversationStateAsync(string conversationId);
        Task<bool> DeleteConversationAsync(string conversationId);
        void ResetConversation();
    }

    public class AssistantService : IAssistantService
    {
        private readonly IEmbeddingService _embeddingService;
        private readonly IMovieService _movieService;
        private readonly IDeepThinkService _deepThinkService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<AssistantService> _logger;
        public string HistoryMessage = "";

        // ЕДИНАЯ беседа вместо словаря
        private ConversationState _currentConversation;

        public AssistantService(
            IEmbeddingService embeddingService,
            IMovieService movieService,
            IDeepThinkService deepThinkService,
            IHttpClientFactory httpClientFactory,
            ILogger<AssistantService> logger)
        {
            _embeddingService = embeddingService;
            _movieService = movieService;
            _deepThinkService = deepThinkService;
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;
        }

        public async Task<AssistantResponse> ProcessMessageAsync(AssistantRequest request, ConversationState conversation)
        {
            try
            {
                // Если запрошен сброс, создаем новую беседу
                if (request.ResetConversation)
                {
                    _currentConversation = CreateNewConversation();
                    _logger.LogInformation("Conversation reset requested, created new conversation");
                }

                // Всегда используем текущую беседу
                _currentConversation = conversation;

                // ДЕБАГ: Логируем текущее состояние диалога
                _logger.LogInformation("Conversation has {MessageCount} messages before processing new message",
                    conversation.Messages.Count);

                // Добавляем сообщение пользователя
                conversation.Messages.Add(new Message
                {
                    Role = "user",
                    Content = request.Message + (request.UseDeepThink ? " [DEEP THINK MODE]" : "")
                });
                HistoryMessage += request.Message + (request.UseDeepThink ? " [DEEP THINK MODE]" : "");
                AssistantResponse response;

                if (request.UseDeepThink)
                {
                    response = await _deepThinkService.ProcessDeepThinkAsync(conversation, request.Message);
                }
                else
                {
                    // ПРОСТАЯ ЛОГИКА: всегда ищем фильмы и показываем 5 штук
                    response = await ProcessSimpleMovieSearch(conversation, request.Message);
                }

                // Сохраняем ответ ассистента
                conversation.Messages.Add(new Message
                {
                    Role = "assistant",
                    Content = response.Response + (response.UsedDeepThink ? " [DEEP THINK RESPONSE]" : "")
                });
                conversation.UpdatedAt = DateTime.UtcNow;

                // ДЕБАГ: Логируем обновленное состояние
                _logger.LogInformation("Conversation now has {MessageCount} messages after processing",
                    conversation.Messages.Count);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing assistant message");
                return new AssistantResponse
                {
                    Response = "Sorry, an error occurred. Please try again.",
                    ConversationId = "single-conversation",
                    UsedDeepThink = request.UseDeepThink
                };
            }
        }

        // МЕТОД: Создание новой беседы
        private ConversationState CreateNewConversation()
        {
            var conversation = new ConversationState();

            // Устанавливаем фиксированный ID для единой беседы
            conversation.ConversationId = "single-conversation";

            conversation.Messages.Add(new Message
            {
                Role = "system",
                Content = "You are a movie recommendation assistant. Always find and recommend exactly 5 movies from the database for every user request."
            });

            _logger.LogInformation("Created new single conversation with ID: {ConversationId}", conversation.ConversationId);
            return conversation;
        }

        // УПРОЩЕННЫЙ МЕТОД: всегда ищем фильмы
        private async Task<AssistantResponse> ProcessSimpleMovieSearch(ConversationState conversation, string userMessage)
        {
            try
            {
                // Шаг 1: Создаем поисковый запрос для эмбеддингов с учетом истории
                var (searchQuery, preferencesInfo) = await CreateSearchQueryWithHistoryAsync(userMessage, conversation);

                // Шаг 2: Ищем фильмы в базе через эмбеддинги
                var searchResults = await _embeddingService.FindSimilarMoviesAsync(searchQuery, 15, "bge-m3", "cosine");
                var movies = searchResults.Select(r => r.Movie).ToList();

                // Шаг 3: Берем ТОЧНО 5 фильмов (даже если нужно дополнить)
                var recommendedMovies = await EnsureFiveMoviesAsync(movies, searchQuery, conversation);

                // Шаг 4: Генерируем ответ с рекомендациями
                var response = await GenerateMovieResponseAsync(conversation, recommendedMovies, searchQuery, userMessage, preferencesInfo);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in simple movie search");
                return CreateFallbackResponse(conversation.ConversationId);
            }
        }

        // МЕТОД: Создаем поисковый запрос для эмбеддингов С УЧЕТОМ ИСТОРИИ
        private async Task<(string SearchQuery, string PreferencesInfo)> CreateSearchQueryWithHistoryAsync(string userMessage, ConversationState conversation)
        {
            // Получаем историю диалога (только пользовательские сообщения)
            var userMessages = conversation.Messages
                .Where(m => m.Role == "user")
                .Select(m => m.Content.Replace(" [DEEP THINK MODE]", ""))
                .ToList();

            var preferencesInfo = new StringBuilder();
            preferencesInfo.AppendLine("🎯 **User Preferences Analysis:**");

            // ДЕБАГ: Логируем детали истории
            _logger.LogInformation("Processing message for conversation. User messages count: {Count}",
                userMessages.Count);

            // Проверяем, действительно ли это первое сообщение
            var isFirstMessage = userMessages.Count <= 1;

            if (isFirstMessage)
            {
                var simpleQuery = await CreateSimpleSearchQueryAsync(userMessage);
                UpdateUserPreferences(conversation, userMessage, simpleQuery);

                preferencesInfo.AppendLine("- First message, no history yet");
                preferencesInfo.AppendLine($"- Current request: `{userMessage}`");
                preferencesInfo.AppendLine($"- Extracted genres: `{string.Join(", ", conversation.Preferences.Genres)}`");

                return (simpleQuery, preferencesInfo.ToString());
            }

            // ЕСТЬ ИСТОРИЯ: используем умный анализ
            var previousUserMessages = userMessages.Take(userMessages.Count - 1).ToList(); // Все кроме текущего
            var currentMessage = userMessage;

            preferencesInfo.AppendLine($"- Previous messages in conversation: `{previousUserMessages.Count}`");
            preferencesInfo.AppendLine($"- Previous user requests: `{string.Join(" → ", previousUserMessages.TakeLast(3))}`");
            preferencesInfo.AppendLine($"- Current request: `{currentMessage}`");
            preferencesInfo.AppendLine($"- Saved preferences: `{string.Join(", ", conversation.Preferences.Genres)}`");

            // Анализируем контекст диалога
            var conversationContext = AnalyzeConversationContext(conversation, currentMessage);

            preferencesInfo.AppendLine($"- Context analysis: `{(conversationContext.IsGenreChange ? "GENRE CHANGE" : "CONTINUATION")}`");

            // Создаем комбинированный запрос на основе истории
            var combinedQuery = await CreateCombinedSearchQueryAsync(currentMessage, conversationContext, conversation);

            // Обновляем предпочтения пользователя
            UpdateUserPreferences(conversation, currentMessage, combinedQuery);

            preferencesInfo.AppendLine($"- Final query: `{combinedQuery}`");
            preferencesInfo.AppendLine($"- Strategy: `{(conversationContext.IsGenreChange ? "COMBINE genres" : "ENHANCE current")}`");
            preferencesInfo.AppendLine($"- Updated preferences: `{string.Join(", ", conversation.Preferences.Genres)}`");

            _logger.LogInformation("Created SMART search query: '{SearchQuery}' for conversation with {HistoryCount} previous messages",
                combinedQuery, previousUserMessages.Count);

            return (combinedQuery, preferencesInfo.ToString());
        }

        // МЕТОД: Анализ контекста диалога (УПРОЩЕННЫЙ И НАДЕЖНЫЙ)
        private ConversationContext AnalyzeConversationContext(ConversationState conversation, string currentMessage)
        {
            var userMessages = conversation.Messages
                .Where(m => m.Role == "user")
                .Select(m => m.Content.Replace(" [DEEP THINK MODE]", ""))
                .ToList();

            var context = new ConversationContext
            {
                PrimaryGenres = conversation.Preferences.Genres.Take(3).ToList(),
                IsContinuation = userMessages.Count > 1
            };

            if (!context.PrimaryGenres.Any())
            {
                context.IsGenreChange = false;
                return context;
            }

            // Анализируем смену жанра
            var currentGenres = ExtractGenresFromText(currentMessage);
            var hasGenreOverlap = context.PrimaryGenres.Any(previousGenre =>
                currentGenres.Any(currentGenre =>
                    currentGenre.Contains(previousGenre) || previousGenre.Contains(currentGenre)));

            context.IsGenreChange = !hasGenreOverlap;

            _logger.LogInformation("Context analysis - Previous: [{Previous}], Current: [{Current}], GenreChange: {IsGenreChange}",
                string.Join(", ", context.PrimaryGenres), string.Join(", ", currentGenres), context.IsGenreChange);

            return context;
        }

        // МЕТОД: Создание комбинированного поискового запроса
        private async Task<string> CreateCombinedSearchQueryAsync(string userMessage, ConversationContext context, ConversationState conversation)
        {
            // Если обнаружена смена жанра, комбинируем с предыдущими предпочтениями
            if (context.IsGenreChange && context.PrimaryGenres.Any())
            {
                var previousGenres = string.Join(" ", context.PrimaryGenres.Take(2));
                var currentQuery = await CreateSimpleSearchQueryAsync(userMessage);

                // Комбинируем: текущий запрос + предыдущие жанры
                var combinedQuery = $"{currentQuery} {previousGenres}";

                _logger.LogInformation("Creating COMBINED query: '{Current}' + '{Previous}' = '{Combined}'",
                    currentQuery, previousGenres, combinedQuery);

                return combinedQuery.Trim();
            }

            // Если жанр тот же, просто улучшаем текущий запрос
            var enhancedQuery = await CreateSimpleSearchQueryAsync(userMessage);

            // Добавляем контекст из предпочтений если есть
            if (context.PrimaryGenres.Any())
            {
                var mainGenre = context.PrimaryGenres.First();
                if (!enhancedQuery.ToLower().Contains(mainGenre))
                {
                    enhancedQuery = $"{enhancedQuery} {mainGenre}";
                }
            }

            _logger.LogInformation("Creating ENHANCED query: '{Enhanced}'", enhancedQuery);
            return enhancedQuery.Trim();
        }

        // МЕТОД: Извлечение жанров из текста
        private List<string> ExtractGenresFromText(string text)
        {
            var genreKeywords = new Dictionary<string, string>
            {
                { "comedy", "comedy" }, { "комедия", "comedy" }, { "комедийные", "comedy" },
                { "drama", "drama" }, { "драма", "drama" }, { "драмы", "drama" },
                { "action", "action" }, { "экшен", "action" }, { "боевик", "action" },
                { "romance", "romance" }, { "роман", "romance" }, { "романтические", "romance" }, { "любовь", "romance" },
                { "horror", "horror" }, { "ужасы", "horror" }, { "хоррор", "horror" },
                { "sci-fi", "sci-fi" }, { "фантастика", "sci-fi" }, { "научно-фантастические", "sci-fi" }, { "научная фантастика", "sci-fi" },
                { "fantasy", "fantasy" }, { "фэнтези", "fantasy" },
                { "thriller", "thriller" }, { "триллер", "thriller" },
                { "mystery", "mystery" }, { "мистика", "mystery" }, { "детектив", "mystery" },
                { "adventure", "adventure" }, { "приключения", "adventure" },
                { "crime", "crime" }, { "криминал", "crime" },
                { "family", "family" }, { "семейный", "family" },
                { "animation", "animation" }, { "анимация", "animation" }, { "мультфильм", "animation" }
            };

            var textLower = text.ToLower();
            var foundGenres = new List<string>();

            foreach (var (keyword, genre) in genreKeywords)
            {
                if (textLower.Contains(keyword) && !foundGenres.Contains(genre))
                {
                    foundGenres.Add(genre);
                }
            }

            return foundGenres;
        }

        // УПРОЩЕННЫЙ МЕТОД для простых запросов
        private async Task<string> CreateSimpleSearchQueryAsync(string userMessage)
        {
            // Если сообщение короткое или простое, используем его как есть
            if (userMessage.Length < 50 && !userMessage.Contains("?"))
            {
                return userMessage;
            }

            var queryPrompt = $@"
User request: {userMessage}

Create a SHORT search query for movie database (2-4 words).
Focus on key keywords for movie search.

Examples:
- ""научно-фантастические фильмы"" -> ""sci-fi""
- ""комедийные фильмы про любовь"" -> ""romantic comedy""
- ""фильмы про космос"" -> ""space""
- ""грустные драмы"" -> ""drama""

Return ONLY the search query:";

            var searchQuery = await CallLlmAsync(queryPrompt, "gemma3:1b", 0.1);

            return string.IsNullOrEmpty(searchQuery) ? userMessage : searchQuery.Trim();
        }

        // МЕТОД: Обновление предпочтений пользователя
        private void UpdateUserPreferences(ConversationState conversation, string userMessage, string searchQuery)
        {
            try
            {
                // Извлекаем жанры из поискового запроса и сообщения
                var foundGenres = ExtractGenresFromText(searchQuery);
                var messageGenres = ExtractGenresFromText(userMessage);

                // Объединяем все найденные жанры
                var allGenres = foundGenres.Union(messageGenres).ToList();

                // Добавляем в предпочтения
                foreach (var genre in allGenres)
                {
                    if (!conversation.Preferences.Genres.Contains(genre))
                    {
                        conversation.Preferences.Genres.Add(genre);
                    }
                }

                // Ограничиваем размер списка
                conversation.Preferences.Genres = conversation.Preferences.Genres
                    .Distinct()
                    .Take(8)
                    .ToList();

                _logger.LogInformation("Updated preferences for conversation: {Genres}",
                    string.Join(", ", conversation.Preferences.Genres));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update user preferences for conversation");
            }
        }

        // МЕТОД: Гарантируем 5 фильмов
        private async Task<List<Movie>> EnsureFiveMoviesAsync(List<Movie> initialMovies, string searchQuery, ConversationState conversation)
        {
            var movies = initialMovies.Take(5).ToList();

            // Если уже есть 5+ фильмов - отлично
            if (movies.Count >= 5)
            {
                return movies.Take(5).ToList();
            }

            _logger.LogInformation("Only found {Count} movies, searching for more...", movies.Count);

            // Пробуем альтернативные поисковые запросы с учетом предпочтений
            var alternativeQueries = new List<string> { searchQuery };

            // Добавляем запросы на основе предпочтений пользователя
            if (conversation.Preferences.Genres.Any())
            {
                var preferredGenres = string.Join(" ", conversation.Preferences.Genres.Take(2));
                alternativeQueries.Add(preferredGenres + " movies");
                alternativeQueries.Add($"{searchQuery} {preferredGenres}");
            }

            // Стандартные запасные варианты
            alternativeQueries.AddRange(new List<string> { "popular", "highly rated", "classic" });

            foreach (var altQuery in alternativeQueries.Distinct())
            {
                if (movies.Count >= 5) break;

                try
                {
                    var additionalResults = await _embeddingService.FindSimilarMoviesAsync(altQuery, 10, "bge-m3", "cosine");
                    var additionalMovies = additionalResults.Select(r => r.Movie)
                        .Where(m => !movies.Any(existing => existing.Id == m.Id))
                        .Take(5 - movies.Count)
                        .ToList();

                    movies.AddRange(additionalMovies);

                    _logger.LogInformation("Added {AddedCount} movies from alternative query: '{AltQuery}'",
                        additionalMovies.Count, altQuery);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to search with alternative query: {AltQuery}", altQuery);
                }
            }

            // Если все еще меньше 5, берем любые популярные фильмы
            if (movies.Count < 5)
            {
                try
                {
                    var popularResults = await _embeddingService.FindSimilarMoviesAsync("popular blockbuster", 10, "bge-m3", "cosine");
                    var popularMovies = popularResults.Select(r => r.Movie)
                        .Where(m => !movies.Any(existing => existing.Id == m.Id))
                        .Take(5 - movies.Count)
                        .ToList();

                    movies.AddRange(popularMovies);

                    _logger.LogInformation("Added {AddedCount} popular movies to reach total of {TotalCount}",
                        popularMovies.Count, movies.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get popular movies");
                }
            }

            return movies.Take(5).ToList();
        }

        // МЕТОД: Генерация ответа с фильмами
        private async Task<AssistantResponse> GenerateMovieResponseAsync(ConversationState conversation, List<Movie> movies, string searchQuery, string userMessage, string preferencesInfo)
        {
            if (!movies.Any())
            {
                return new AssistantResponse
                {
                    Response = $"{preferencesInfo}\n\n🔍 **Search query used**: `{searchQuery}`\n\nI couldn't find any movies matching your request.",
                    ConversationId = conversation.ConversationId,
                    RecommendedMovies = new List<Movie>(),
                    UsedDeepThink = false,
                    EmbeddingQuery = searchQuery
                };
            }

            var responseBuilder = new StringBuilder();

            // Показываем анализ предпочтений ПЕРВЫМ делом
            responseBuilder.AppendLine(preferencesInfo);
            responseBuilder.AppendLine();
            responseBuilder.AppendLine("History: " + HistoryMessage);
            responseBuilder.AppendLine($"🔍 **Final search query**: `{searchQuery}`");

            //// Анализируем связь с историей
            //var hasHistory = conversation.Messages.Count(m => m.Role == "user") > 1;
            //var previousGenres = conversation.Preferences.Genres.Take(3).ToList();

            //if (hasHistory && previousGenres.Any())
            //{
            //    var currentGenres = ExtractGenresFromText(searchQuery);
            //    var isGenreTransition = !currentGenres.Any(g => previousGenres.Contains(g));

            //    if (isGenreTransition)
            //    {
            //        responseBuilder.AppendLine($"🔄 **Genre transition detected**: Adding your previous interests (`{string.Join(", ", previousGenres)}`) to current request");
            //    }
            //    else
            //    {
            //        responseBuilder.AppendLine($"🎯 **Building on your preferences**: `{string.Join(", ", previousGenres)}`");
            //    }
            //}

            responseBuilder.AppendLine();

            // Генерируем персонализированный ответ
            var responsePrompt = $@"
USER'S CURRENT REQUEST: {userMessage}
SEARCH QUERY USED: {searchQuery}
USER'S PREFERENCES FROM HISTORY: {HistoryMessage}

MOVIES TO RECOMMEND:
{string.Join("\n", movies.Select((m, i) => $"{i + 1}. {m.Title} ({m.ReleaseDate?.Year}) - {FormatGenres(m.Genres)}"))}

INSTRUCTIONS:
- Recommend all {movies.Count} movies using EXACT titles
- Explain briefly why these match the request
- {(0==0 ? "Mention how these connect to their previous interests if relevant" : "Keep it focused on current request")}
- Make it sound natural and personalized
- End by asking if they want more specific recommendations

RESPONSE:";

            var recommendationText = await CallLlmAsync(responsePrompt, "gemma3:1b", 0.7);
            responseBuilder.AppendLine(recommendationText);

            return new AssistantResponse
            {
                Response = responseBuilder.ToString(),
                RecommendedMovies = movies,
                ConversationId = conversation.ConversationId,
                UsedDeepThink = false,
                EmbeddingQuery = searchQuery
            };
        }

        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        private string FormatGenres(string genresJson)
        {
            if (string.IsNullOrEmpty(genresJson)) return "Various";
            try
            {
                if (genresJson.TrimStart().StartsWith('['))
                {
                    var genres = JsonSerializer.Deserialize<List<Genre>>(genresJson);
                    return genres != null && genres.Any()
                        ? string.Join(", ", genres.Take(2).Select(g => g.Name))
                        : "Various";
                }
                return genresJson.Length > 50 ? genresJson.Substring(0, 50) + "..." : genresJson;
            }
            catch
            {
                return "Various";
            }
        }

        private AssistantResponse CreateFallbackResponse(string conversationId)
        {
            return new AssistantResponse
            {
                Response = "🎯 **User Preferences Analysis:**\n- No preferences data available\n\n🔍 **Search query**: `fallback`\n\nI found some movie recommendations for you!",
                ConversationId = conversationId,
                RecommendedMovies = new List<Movie>(),
                UsedDeepThink = false,
                EmbeddingQuery = "fallback"
            };
        }

        private async Task<string> CallLlmAsync(string prompt, string model = "gemma3:1b", double temperature = 0.7)
        {
            try
            {
                var request = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = temperature, num_predict = 300 }
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
                        return responseProperty.GetString()?.Trim() ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling LLM");
            }

            return string.Empty;
        }

        public Task<ConversationState?> GetConversationStateAsync(string conversationId)
        {
            // Всегда возвращаем текущую беседу, игнорируем conversationId
            return Task.FromResult<ConversationState?>(_currentConversation);
        }

        public Task<bool> DeleteConversationAsync(string conversationId)
        {
            // При "удалении" просто сбрасываем беседу
            ResetConversation();
            return Task.FromResult(true);
        }

        public void ResetConversation()
        {
            _currentConversation = CreateNewConversation();
            _logger.LogInformation("Conversation reset");
        }
    }

    // Классы моделей
    public class ConversationContext
    {
        public List<string> PrimaryGenres { get; set; } = new List<string>();
        public List<string> SecondaryGenres { get; set; } = new List<string>();
        public List<string> Themes { get; set; } = new List<string>();
        public List<string> Preferences { get; set; } = new List<string>();
        public string Mood { get; set; } = string.Empty;
        public bool IsContinuation { get; set; }
        public bool IsGenreChange { get; set; }
    }

    public class Genre
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}