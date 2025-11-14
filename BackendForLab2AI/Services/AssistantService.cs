// AssistantService.cs
using BackendForLab2AI.Models;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

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
        private readonly HttpClient _httpClient;
        private readonly ILogger<AssistantService> _logger;
        private readonly Dictionary<string, ConversationState> _conversations;

        public AssistantService(
            IEmbeddingService embeddingService,
            IMovieService movieService,
            IHttpClientFactory httpClientFactory,
            ILogger<AssistantService> logger)
        {
            _embeddingService = embeddingService;
            _movieService = movieService;
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

                // Добавляем сообщение пользователя
                conversation.Messages.Add(new Message
                {
                    Role = "user",
                    Content = request.Message
                });

                // Анализируем запрос и обновляем предпочтения
                await AnalyzeUserPreferencesAsync(conversation, request.Message);

                // Ищем релевантные фильмы через RAG
                var relevantMovies = await FindRelevantMoviesAsync(conversation);

                // Генерируем ответ с рекомендациями
                var response = await GenerateAssistantResponseAsync(conversation, relevantMovies);

                // Сохраняем ответ ассистента
                conversation.Messages.Add(new Message
                {
                    Role = "assistant",
                    Content = response.Response
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
                    ConversationId = request.ConversationId ?? Guid.NewGuid().ToString()
                };
            }
        }

        private async Task<ConversationState> GetOrCreateConversationAsync(AssistantRequest request)
        {
            if (request.ResetConversation || string.IsNullOrEmpty(request.ConversationId))
            {
                var newConversation = new ConversationState();
                _conversations[newConversation.ConversationId] = newConversation;

                // Добавляем системный промпт для начала беседы
                var systemPrompt = await GetSystemPromptAsync();
                newConversation.Messages.Add(new Message
                {
                    Role = "system",
                    Content = systemPrompt
                });

                return newConversation;
            }

            if (_conversations.TryGetValue(request.ConversationId, out var conversation))
            {
                return conversation;
            }

            // Если разговор не найден, создаем новый
            return await GetOrCreateConversationAsync(new AssistantRequest
            {
                ResetConversation = true
            });
        }

        private async Task<string> GetSystemPromptAsync()
        {
            return @"Ты - дружелюбный ассистент по рекомендации фильмов. Твоя задача - помочь пользователю найти фильмы, которые ему понравятся.

Правила:
1. Задавай открытые вопросы чтобы понять предпочтения пользователя
2. Учитывай жанры, настроение, временные периоды, язык и длительность
3. Рекомендуй конкретные фильмы с кратким объяснением почему они подходят
4. Если информации недостаточно - задавай уточняющие вопросы
5. Будь энтузиастичным и полезным

Начни с приветствия и спроси о предпочтениях в фильмах.";
        }

        private async Task AnalyzeUserPreferencesAsync(ConversationState conversation, string userMessage)
        {
            try
            {
                var analysisPrompt = $@"Проанализируй сообщение пользователя о фильмах и извлеки предпочтения.
Сообщение: {userMessage}

Извлеки:
1. Жанры (комедия, драма, боевик, фантастика и т.д.)
2. Настроение (веселое, грустное, напряженное, романтичное)
3. Временной период (старые, новые, 90-е, 2000-е)
4. Языковые предпочтения
5. Длительность (короткие, длинные)
6. Упомянутые фильмы (понравившиеся или нет)

Ответ в формате JSON:
{{
    ""genres"": [""жанр1"", ""жанр2""],
    ""moods"": [""настроение1"", ""настроение2""],
    ""timePeriod"": ""период или null"",
    ""language"": ""язык или null"",
    ""runtime"": число_минут или null,
    ""likedMovies"": [""фильм1"", ""фильм2""],
    ""dislikedMovies"": [""фильм1"", ""фильм2""]
}}";

                var analysis = await CallLlmAsync(analysisPrompt, "llama3.1", 0.3);

                // Парсим JSON и обновляем предпочтения
                await UpdatePreferencesFromAnalysis(conversation.Preferences, analysis);
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
                using var document = JsonDocument.Parse(analysisJson);
                var root = document.RootElement;

                if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                {
                    preferences.Genres.AddRange(genres.EnumerateArray().Select(g => g.GetString() ?? ""));
                }

                if (root.TryGetProperty("moods", out var moods) && moods.ValueKind == JsonValueKind.Array)
                {
                    preferences.Moods.AddRange(moods.EnumerateArray().Select(m => m.GetString() ?? ""));
                }

                if (root.TryGetProperty("timePeriod", out var timePeriod) && timePeriod.ValueKind == JsonValueKind.String)
                {
                    preferences.TimePeriod = timePeriod.GetString();
                }

                if (root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String)
                {
                    preferences.LanguagePreference = language.GetString();
                }

                if (root.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number)
                {
                    preferences.DesiredRuntime = runtime.GetInt32();
                }

                if (root.TryGetProperty("likedMovies", out var likedMovies) && likedMovies.ValueKind == JsonValueKind.Array)
                {
                    preferences.PreviouslyLikedMovies.AddRange(
                        likedMovies.EnumerateArray().Select(m => m.GetString() ?? ""));
                }

                if (root.TryGetProperty("dislikedMovies", out var dislikedMovies) && dislikedMovies.ValueKind == JsonValueKind.Array)
                {
                    preferences.AvoidedMovies.AddRange(
                        dislikedMovies.EnumerateArray().Select(m => m.GetString() ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating preferences from analysis");
            }
        }

        private async Task<List<Movie>> FindRelevantMoviesAsync(ConversationState conversation)
        {
            var relevantMovies = new List<Movie>();

            try
            {
                // Поиск по предпочтениям через RAG
                var searchQueries = BuildSearchQueries(conversation.Preferences);

                foreach (var query in searchQueries)
                {
                    var movies = await _embeddingService.FindSimilarMoviesAsync(
                        query, 10, "nomic-embed-text", "cosine");

                    relevantMovies.AddRange(movies.Select(m => m.Movie));
                }

                // Фильтрация по дополнительным критериям
                relevantMovies = relevantMovies
                    .DistinctBy(m => m.Id)
                    .Where(m => FilterByPreferences(m, conversation.Preferences))
                    .Take(20)
                    .ToList();

                _logger.LogInformation("Found {Count} relevant movies for conversation {ConversationId}",
                    relevantMovies.Count, conversation.ConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding relevant movies");
            }

            return relevantMovies;
        }

        private List<string> BuildSearchQueries(UserPreferences preferences)
        {
            var queries = new List<string>();

            // Базовые запросы по жанрам и настроению
            if (preferences.Genres.Any())
            {
                queries.Add($"фильмы жанры {string.Join(" ", preferences.Genres)}");
            }

            if (preferences.Moods.Any())
            {
                queries.Add($"фильмы настроение {string.Join(" ", preferences.Moods)}");
            }

            // Запросы по понравившимся фильмам
            foreach (var likedMovie in preferences.PreviouslyLikedMovies.Take(3))
            {
                queries.Add($"фильмы похожие на {likedMovie}");
            }

            // Комбинированные запросы
            if (preferences.Genres.Any() && preferences.Moods.Any())
            {
                queries.Add($"{string.Join(" ", preferences.Genres)} {string.Join(" ", preferences.Moods)} фильмы");
            }

            return queries;
        }

        private bool FilterByPreferences(Movie movie, UserPreferences preferences)
        {
            // Фильтрация по языку
            if (!string.IsNullOrEmpty(preferences.LanguagePreference) &&
                !string.IsNullOrEmpty(movie.OriginalLanguage) &&
                !movie.OriginalLanguage.Equals(preferences.LanguagePreference, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Фильтрация по году выпуска
            if (!string.IsNullOrEmpty(preferences.TimePeriod) &&
                movie.ReleaseDate.HasValue)
            {
                var year = movie.ReleaseDate.Value.Year;
                if (preferences.TimePeriod.Contains("старые") && year > 2000) return false;
                if (preferences.TimePeriod.Contains("новые") && year < 2010) return false;
                if (preferences.TimePeriod.Contains("90") && (year < 1990 || year > 1999)) return false;
                if (preferences.TimePeriod.Contains("2000") && (year < 2000 || year > 2009)) return false;
            }

            // Фильтрация по длительности
            if (preferences.DesiredRuntime.HasValue && movie.Runtime.HasValue)
            {
                var diff = Math.Abs(movie.Runtime.Value - preferences.DesiredRuntime.Value);
                if (diff > 30) return false; // Разница более 30 минут
            }

            return true;
        }

        private async Task<AssistantResponse> GenerateAssistantResponseAsync(
            ConversationState conversation, List<Movie> relevantMovies)
        {
            var context = BuildConversationContext(conversation, relevantMovies);

            var responsePrompt = $@"{context}

Текущий разговор:
{string.Join("\n", conversation.Messages.Skip(1).Select(m => $"{m.Role}: {m.Content}"))}

Инструкции:
1. Ответь естественно и дружелюбно
2. Если нашлось мало фильмов или нужно уточнение - задай вопросы
3. Рекомендуй 3-5 самых подходящих фильмов с кратким объяснением
4. Учитывай историю разговора
5. Не упоминай технические детали поиска

Твой ответ:";

            var llmResponse = await CallLlmAsync(responsePrompt, "llama3.1", 0.7);

            return new AssistantResponse
            {
                Response = llmResponse,
                RecommendedMovies = relevantMovies.Take(5).ToList(),
                ConversationId = conversation.ConversationId,
                NeedsClarification = relevantMovies.Count < 3,
                ClarificationQuestions = GenerateClarificationQuestions(conversation.Preferences)
            };
        }

        private string BuildConversationContext(ConversationState conversation, List<Movie> movies)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Информация о пользователе:");
            sb.AppendLine($"- Предпочтительные жанры: {string.Join(", ", conversation.Preferences.Genres)}");
            sb.AppendLine($"- Настроение: {string.Join(", ", conversation.Preferences.Moods)}");
            if (!string.IsNullOrEmpty(conversation.Preferences.TimePeriod))
                sb.AppendLine($"- Временной период: {conversation.Preferences.TimePeriod}");
            if (!string.IsNullOrEmpty(conversation.Preferences.LanguagePreference))
                sb.AppendLine($"- Язык: {conversation.Preferences.LanguagePreference}");

            sb.AppendLine();
            sb.AppendLine("Найденные фильмы для рекомендации:");
            foreach (var movie in movies.Take(10))
            {
                var overview = movie.Overview ?? "";
                var shortOverview = overview.Length > 100 ? overview.Substring(0, 100) + "..." : overview;
                sb.AppendLine($"- {movie.Title} ({movie.ReleaseDate?.Year}): {shortOverview}");
                if (!string.IsNullOrEmpty(movie.Genres))
                    sb.AppendLine($"  Жанры: {movie.Genres}");
                if (movie.Runtime.HasValue)
                    sb.AppendLine($"  Длительность: {movie.Runtime} мин");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private List<string> GenerateClarificationQuestions(UserPreferences preferences)
        {
            var questions = new List<string>();

            if (!preferences.Genres.Any())
                questions.Add("Какие жанры фильмов вы предпочитаете? Например, комедия, драма, фантастика...");

            if (!preferences.Moods.Any())
                questions.Add("Какое настроение вы ищете? Веселое, романтичное, напряженное, вдохновляющее?");

            if (string.IsNullOrEmpty(preferences.TimePeriod))
                questions.Add("Вас интересуют новые фильмы или классика какого-то периода?");

            if (preferences.PreviouslyLikedMovies.Count < 2)
                questions.Add("Какие фильмы вам особенно понравились в последнее время?");

            return questions.Take(2).ToList();
        }

        private async Task<string> CallLlmAsync(string prompt, string model = "llama3", double temperature = 0.7)
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

                // Пробуем разные endpoint'ы
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
                                var result = responseProperty.GetString() ?? "Нет ответа";
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

                return "Привет! Я ваш ассистент по рекомендации фильмов. Расскажите, какие фильмы вам нравятся?";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling LLM");
                return "Извините, произошла ошибка. Пожалуйста, попробуйте еще раз.";
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
}