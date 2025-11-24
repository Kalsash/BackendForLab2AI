// Services/AssistantService.cs
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
        private readonly IMovieSearchToolService _movieSearchTool;
        private readonly IDeepThinkService _deepThinkService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<AssistantService> _logger;

        public string HistoryMessage = "";
        private ConversationState _currentConversation;

        public AssistantService(
            IEmbeddingService embeddingService,
            IMovieSearchToolService movieSearchTool,
            IDeepThinkService deepThinkService,
            IHttpClientFactory httpClientFactory,
            ILogger<AssistantService> logger)
        {
            _embeddingService = embeddingService;
            _movieSearchTool = movieSearchTool;
            _deepThinkService = deepThinkService;
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;
            _currentConversation = CreateNewConversation();
        }

        public async Task<AssistantResponse> ProcessMessageAsync(AssistantRequest request, ConversationState conversation)
        {
            try
            {
                if (request.ResetConversation)
                {
                    _currentConversation = CreateNewConversation();
                    _logger.LogInformation("Conversation reset requested, created new conversation");
                }

                _currentConversation = conversation;

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
                    // Используем интеллектуальный поиск с настоящим tool calling
                    response = await ProcessWithToolCallingAsync(conversation, request.Message);
                }

                // Сохраняем ответ ассистента
                conversation.Messages.Add(new Message
                {
                    Role = "assistant",
                    Content = response.Response + (response.UsedDeepThink ? " [DEEP THINK RESPONSE]" : "")
                });
                conversation.UpdatedAt = DateTime.UtcNow;

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

        // ОСНОВНОЙ МЕТОД: Настоящий Tool Calling
        private async Task<AssistantResponse> ProcessWithToolCallingAsync(ConversationState conversation, string userMessage)
        {
            try
            {
                _logger.LogInformation("Starting tool calling for: {UserMessage}", userMessage);

                // 1. Создаем описание инструментов
                var toolsDescription = CreateToolsDescription();

                // 2. Формируем системный промпт с инструментами
                var systemPrompt = CreateSystemPromptWithTools();

                // 3. Отправляем запрос к LLM с инструментами
                var llmResponse = await CallLlmWithToolsAsync(systemPrompt, userMessage, conversation);

                // 4. Обрабатываем ответ LLM - проверяем, хочет ли она использовать инструменты
                var toolCalls = ExtractToolCalls(llmResponse);

                if (toolCalls.Any())
                {
                    _logger.LogInformation("LLM decided to use {Count} tools", toolCalls.Count);

                    // 5. Исполняем вызовы инструментов
                    var toolResults = await ExecuteToolCallsAsync(toolCalls, userMessage);

                    // 6. Генерируем финальный ответ на основе результатов инструментов
                    return await GenerateFinalResponseWithToolsAsync(toolResults, userMessage, conversation, toolCalls);
                }
                else
                {
                    _logger.LogInformation("LLM decided to respond directly without tools");

                    // 7. Если инструменты не нужны - генерируем прямой ответ
                    return await GenerateDirectResponseAsync(userMessage, conversation, llmResponse);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in tool calling process");
                return await ProcessFallbackSearchAsync(conversation, userMessage);
            }
        }

        // МЕТОД: Создание описания инструментов
        private string CreateToolsDescription()
        {
            return @"
AVAILABLE TOOLS:

1. search_movies - Search movies by keywords, titles, or descriptions
   Usage: When user wants to find movies by specific keywords, titles, or themes
   Parameters: query (string), limit (number, default: 5)

2. search_by_genre - Search movies by specific genre
   Usage: When user mentions specific genres like 'comedy', 'drama', 'action'
   Parameters: genre (string), limit (number, default: 5)

3. search_by_mood - Search movies by mood or feeling
   Usage: When user describes mood like 'relaxing', 'exciting', 'funny', 'emotional'
   Parameters: mood (string), limit (number, default: 5)

4. find_similar_movies - Find movies similar to a description
   Usage: When user says 'similar to X', 'like X', or describes movie qualities
   Parameters: description (string), limit (number, default: 5)

TOOL CALLING FORMAT:
If you need to use a tool, respond in this exact format:

THOUGHT: [Your reasoning about whether to use tools and which ones]
USE_TOOL: [tool_name]
TOOL_PARAMS: [JSON parameters]
CONTINUE: [Your message while waiting for tool results]

Example:
THOUGHT: User wants comedy movies, so I should use search_by_genre tool
USE_TOOL: search_by_genre
TOOL_PARAMS: {""genre"": ""comedy"", ""limit"": 5}
CONTINUE: I'll find some great comedy movies for you!

If you don't need tools, respond normally.";
        }

        // МЕТОД: Создание системного промпта с инструментами
        private string CreateSystemPromptWithTools()
        {
            return @"You are a movie recommendation assistant with access to search tools.

IMPORTANT INSTRUCTIONS:
- Always try to find and recommend exactly 5 movies from the database
- Use search tools when user asks for movie recommendations
- Choose the most appropriate tool based on the request
- If tools don't find enough movies, you can try alternative searches
- Be conversational and helpful

" + CreateToolsDescription();
        }

        // МЕТОД: Вызов LLM с инструментами
        private async Task<string> CallLlmWithToolsAsync(string systemPrompt, string userMessage, ConversationState conversation)
        {
            var fullPrompt = $@"
{systemPrompt}

CONVERSATION HISTORY: {HistoryMessage}
USER PREFERENCES: {string.Join(", ", conversation.Preferences.Genres)}

CURRENT REQUEST: {userMessage}

Please analyze the request and decide if you need to use search tools. Remember to use the exact tool calling format if you need tools.";

            return await CallLlmAsync(fullPrompt, "gemma3:1b", 0.3);
        }

        // МЕТОД: Извлечение вызовов инструментов из ответа LLM
        private List<ToolCall> ExtractToolCalls(string llmResponse)
        {
            var toolCalls = new List<ToolCall>();

            if (string.IsNullOrEmpty(llmResponse))
                return toolCalls;

            var lines = llmResponse.Split('\n');
            ToolCall currentTool = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("USE_TOOL:"))
                {
                    if (currentTool != null)
                        toolCalls.Add(currentTool);

                    currentTool = new ToolCall
                    {
                        ToolName = line.Replace("USE_TOOL:", "").Trim()
                    };
                }
                else if (line.StartsWith("TOOL_PARAMS:") && currentTool != null)
                {
                    try
                    {
                        var jsonParams = line.Replace("TOOL_PARAMS:", "").Trim();
                        currentTool.Parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonParams)
                                              ?? new Dictionary<string, object>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse tool parameters: {Line}", line);
                    }
                }
                else if (line.StartsWith("CONTINUE:") && currentTool != null)
                {
                    currentTool.ContinueMessage = line.Replace("CONTINUE:", "").Trim();
                }
            }

            if (currentTool != null)
                toolCalls.Add(currentTool);

            return toolCalls;
        }

        // МЕТОД: Исполнение вызовов инструментов
        private async Task<List<ToolResult>> ExecuteToolCallsAsync(List<ToolCall> toolCalls, string query)
        {
            var results = new List<ToolResult>();

            foreach (var toolCall in toolCalls)
            {
                try
                {
                    _logger.LogInformation("Executing tool: {ToolName} with params: {Params}",
                        toolCall.ToolName, JsonSerializer.Serialize(toolCall.Parameters));

                    List<Movie> movies = new List<Movie>();
                  //  movies = await _movieSearchTool.SearchMoviesAsync(query, 5);
                    switch (toolCall.ToolName.ToLower())
                    {
                        case "search_movies":
                            var limit = GetLimitFromParams(toolCall.Parameters);
                            movies = await _movieSearchTool.SearchMoviesAsync(query, limit);
                            break;

                        case "search_by_genre":
                            var genre = toolCall.Parameters.GetValueOrDefault("genre")?.ToString() ?? query;
                            limit = GetLimitFromParams(toolCall.Parameters);
                            movies = await _movieSearchTool.SearchByGenreAsync(genre, limit);
                            break;

                        case "search_by_mood":
                            var mood = toolCall.Parameters.GetValueOrDefault("mood")?.ToString() ?? query;
                            limit = GetLimitFromParams(toolCall.Parameters);
                            movies = await _movieSearchTool.SearchByMoodAsync(mood, limit);
                            break;

                        case "find_similar_movies":
                            var description = toolCall.Parameters.GetValueOrDefault("description")?.ToString() ?? query;
                            limit = GetLimitFromParams(toolCall.Parameters);
                            movies = await _movieSearchTool.FindSimilarMoviesAsync(description, limit);
                            break;

                        default:
                            _logger.LogWarning("Unknown tool: {ToolName}", toolCall.ToolName);
                            break;
                    }

                    results.Add(new ToolResult
                    {
                        ToolCall = toolCall,
                        Movies = movies,
                        Success = movies.Any()
                    });

                    _logger.LogInformation("Tool {ToolName} executed successfully, found {Count} movies",
                        toolCall.ToolName, movies.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing tool: {ToolName}", toolCall.ToolName);
                    results.Add(new ToolResult
                    {
                        ToolCall = toolCall,
                        Movies = new List<Movie>(),
                        Success = false,
                        Error = ex.Message
                    });
                }
            }

            return results;
        }

        // МЕТОД: Генерация финального ответа с результатами инструментов
        private async Task<AssistantResponse> GenerateFinalResponseWithToolsAsync(
            List<ToolResult> toolResults,
            string userMessage,
            ConversationState conversation,
            List<ToolCall> toolCalls)
        {
            try
            {
                // Собираем все найденные фильмы
                var allMovies = toolResults
                    .Where(r => r.Success)
                    .SelectMany(r => r.Movies)
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .Take(5)
                    .ToList();

                // Гарантируем 5 фильмов
                var finalMovies = await EnsureFiveMoviesAsync(allMovies, userMessage);

                // Обновляем предпочтения пользователя
                UpdateUserPreferences(conversation, userMessage, userMessage);

                // Генерируем интеллектуальный ответ
                var response = await GenerateToolEnhancedResponseAsync(finalMovies, userMessage, toolResults, toolCalls);

                // Генерируем уточняющие вопросы
                var followUpQuestions = await GenerateFollowUpQuestionsAsync(finalMovies, userMessage, toolCalls);

                return new AssistantResponse
                {
                    Response = response,
                    RecommendedMovies = finalMovies,
                    ConversationId = conversation.ConversationId,
                    UsedDeepThink = false,
                    EmbeddingQuery = userMessage,
                    FollowUpQuestions = followUpQuestions,
                    ReasoningContext = $"Used tools: {string.Join(", ", toolCalls.Select(t => t.ToolName))}",
                    ToolCalls = toolCalls,
                    ToolResults = toolResults
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating final response with tools");
                return await ProcessFallbackSearchAsync(conversation, userMessage);
            }
        }

        // МЕТОД: Генерация улучшенного ответа с учетом инструментов
        private async Task<string> GenerateToolEnhancedResponseAsync(
            List<Movie> movies,
            string userMessage,
            List<ToolResult> toolResults,
            List<ToolCall> toolCalls)
        {
            if (!movies.Any())
            {
                return "I searched for movies using several approaches, but couldn't find good matches for your request. Could you try different keywords or be more specific?";
            }

            var toolsUsed = string.Join(", ", toolCalls.Select(t => t.ToolName));
            var moviesInfo = string.Join("\n", movies.Select((m, i) =>
                $"{i + 1}. {m.Title} ({m.ReleaseDate?.Year}) - {FormatGenres(m.Genres)}"));

            var prompt = $@"
USER REQUEST: ""{userMessage}""
TOOLS USED: {toolsUsed}
MOVIES FOUND: {movies.Count}

MOVIE RESULTS:
{moviesInfo}

CRITICAL FORMATTING RULES:
- ALWAYS include the release year in parentheses after each movie title
- Example: ""Inception (2010)"", NOT just ""Inception""
- This is mandatory for every movie mention

Create a friendly, conversational response that:
1. Acknowledges the user's request
2. Mentions that you searched using appropriate tools
3. Highlights the recommended movies
4. Explains why these movies match their request
5. Invites further conversation

Make it sound natural and enthusiastic!
RESPONSE (with years):";

            var response = await CallLlmAsync(prompt, "gemma3:1b", 0.7);
            return response ?? CreateDefaultResponse(movies, userMessage);
        }

        // МЕТОД: Генерация уточняющих вопросов
        private async Task<string> GenerateFollowUpQuestionsAsync(List<Movie> movies, string userMessage, List<ToolCall> toolCalls)
        {
            if (!movies.Any()) return string.Empty;

            try
            {
                var toolsUsed = string.Join(", ", toolCalls.Select(t => t.ToolName));
                var prompt = $@"
Based on:
- User's request: ""{userMessage}""
- Tools used: {toolsUsed}
- Recommended movies: {string.Join(", ", movies.Take(5).Select(m => $"{m.Title} ({m.ReleaseDate?.Year})"))}

Generate 2-3 engaging, open-ended questions that will help understand the user's movie preferences better for future recommendations.

Make the questions:
- Conversational and natural
- Open-ended (not yes/no)
- Relevant to the current context
- Helpful for improving recommendations

QUESTIONS:";

                var questions = await CallLlmAsync(prompt, "gemma3:1b", 0.6);
                return questions?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate follow-up questions");
                return string.Empty;
            }
        }

        // МЕТОД: Прямой ответ без инструментов
        private async Task<AssistantResponse> GenerateDirectResponseAsync(string userMessage, ConversationState conversation, string llmResponse)
        {
            // Для простых запросов, не требующих поиска
            return new AssistantResponse
            {
                Response = llmResponse,
                RecommendedMovies = new List<Movie>(),
                ConversationId = conversation.ConversationId,
                UsedDeepThink = false,
                ReasoningContext = "Direct response without tools"
            };
        }

        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ

        private int GetLimitFromParams(Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey("limit") && int.TryParse(parameters["limit"]?.ToString(), out int limit))
            {
                return limit;
            }
            return 5;
        }

        private async Task<List<Movie>> EnsureFiveMoviesAsync(List<Movie> initialMovies, string searchQuery)
        {
            var movies = initialMovies.Take(5).ToList();

            if (movies.Count >= 5)
            {
                return movies;
            }

            _logger.LogInformation("Only found {Count} movies with tools, searching for more...", movies.Count);

            // Дополнительный поиск если инструменты нашли мало фильмов
            try
            {
                var additionalMovies = await _movieSearchTool.SearchMoviesAsync(searchQuery, 5 - movies.Count);
                var newMovies = additionalMovies
                    .Where(m => !movies.Any(existing => existing.Id == m.Id))
                    .Take(5 - movies.Count)
                    .ToList();

                movies.AddRange(newMovies);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to find additional movies");
            }

            return movies.Take(5).ToList();
        }

        private string CreateDefaultResponse(List<Movie> movies, string userMessage)
        {
            var response = new StringBuilder();
            response.AppendLine($"Based on your request \"{userMessage}\", I found these great movies for you:\n");

            foreach (var (movie, index) in movies.Select((m, i) => (m, i)))
            {
                response.AppendLine($"{index + 1}. **{movie.Title}** ({movie.ReleaseDate?.Year})");
                response.AppendLine($"   - {FormatGenres(movie.Genres)}");
                if (!string.IsNullOrEmpty(movie.Overview) && movie.Overview.Length > 100)
                {
                    response.AppendLine($"   - {movie.Overview.Substring(0, 100)}...");
                }
                response.AppendLine();
            }

            response.AppendLine("Which of these sounds interesting? I can tell you more about any of them!");
            return response.ToString();
        }

        private void UpdateUserPreferences(ConversationState conversation, string userMessage, string searchQuery)
        {
            try
            {
                var foundGenres = ExtractGenresFromText(searchQuery);
                var messageGenres = ExtractGenresFromText(userMessage);
                var allGenres = foundGenres.Union(messageGenres).ToList();

                foreach (var genre in allGenres)
                {
                    if (!conversation.Preferences.Genres.Contains(genre))
                    {
                        conversation.Preferences.Genres.Add(genre);
                    }
                }

                conversation.Preferences.Genres = conversation.Preferences.Genres
                    .Distinct()
                    .Take(8)
                    .ToList();

                _logger.LogInformation("Updated preferences: {Genres}", string.Join(", ", conversation.Preferences.Genres));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update user preferences");
            }
        }

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

        // ФОЛБЭК МЕТОД: Простой поиск
        private async Task<AssistantResponse> ProcessFallbackSearchAsync(ConversationState conversation, string userMessage)
        {
            _logger.LogWarning("Using fallback search method");

            try
            {
                var searchQuery = CreateSimpleSearchQuery(userMessage);
                var movies = await _movieSearchTool.SearchMoviesAsync(searchQuery, 5);

                var response = await GenerateSimpleMovieResponseAsync(movies, searchQuery, userMessage);

                return new AssistantResponse
                {
                    Response = response,
                    RecommendedMovies = movies,
                    ConversationId = conversation.ConversationId,
                    UsedDeepThink = false,
                    EmbeddingQuery = searchQuery,
                    ReasoningContext = "Fallback search method"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fallback search");
                return CreateFallbackResponse(conversation.ConversationId);
            }
        }

        private string CreateSimpleSearchQuery(string userMessage)
        {
            var stopWords = new HashSet<string> {
                "хочу", "мне", "нужны", "фильмы", "кино", "посоветуй",
                "найти", "какой", "что", "какие", "please", "recommend", "movie", "movies"
            };

            var words = userMessage.ToLower()
                .Split(' ', ',', '.', '!', '?')
                .Where(word => word.Length > 2 && !stopWords.Contains(word))
                .Take(3);

            return string.Join(" ", words) ?? "popular";
        }

        private async Task<string> GenerateSimpleMovieResponseAsync(List<Movie> movies, string searchQuery, string userMessage)
        {
            var prompt = $@"
User asked: {userMessage}
Search query: {searchQuery}

Recommended movies:
{string.Join("\n", movies.Select((m, i) => $"{i + 1}. {m.Title} ({m.ReleaseDate?.Year}) - {FormatGenres(m.Genres)}"))}

Create a friendly, conversational response recommending these movies:";

            return await CallLlmAsync(prompt, "gemma3:1b", 0.7);
        }

        // МЕТОД: Создание новой беседы
        private ConversationState CreateNewConversation()
        {
            var conversation = new ConversationState();
            conversation.ConversationId = "single-conversation";

            conversation.Messages.Add(new Message
            {
                Role = "system",
                Content = "You are a movie recommendation assistant with access to search tools. Always try to find and recommend exactly 5 movies from the database for every user request."
            });

            _logger.LogInformation("Created new single conversation with ID: {ConversationId}", conversation.ConversationId);
            return conversation;
        }

        private AssistantResponse CreateFallbackResponse(string conversationId)
        {
            return new AssistantResponse
            {
                Response = "I found some great movie recommendations for you! Here are some popular choices that might interest you.",
                ConversationId = conversationId,
                RecommendedMovies = new List<Movie>(),
                UsedDeepThink = false,
                EmbeddingQuery = "fallback"
            };
        }

        public Task<ConversationState?> GetConversationStateAsync(string conversationId)
        {
            return Task.FromResult<ConversationState?>(_currentConversation);
        }

        public Task<bool> DeleteConversationAsync(string conversationId)
        {
            ResetConversation();
            return Task.FromResult(true);
        }

        public void ResetConversation()
        {
            _currentConversation = CreateNewConversation();
            _logger.LogInformation("Conversation reset");
        }
    }


    // Существующие вспомогательные классы
    public class SearchStrategy
    {
        public string SearchType { get; set; } = "general";
        public string SearchQuery { get; set; } = string.Empty;
        public string Reasoning { get; set; } = string.Empty;
    }

    public class UserIntentAnalysis
    {
        public string RawAnalysis { get; set; } = string.Empty;
        public bool IsMoodBased { get; set; }
        public bool IsGenreSpecific { get; set; }
        public bool RequiresCreativeMatching { get; set; }
        public List<string> DetectedGenres { get; set; } = new List<string>();
        public string DetectedMood { get; set; } = string.Empty;
        public List<string> KeyThemes { get; set; } = new List<string>();
    }

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