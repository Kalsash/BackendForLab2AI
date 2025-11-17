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
        private readonly HttpClient _httpClient;
        private readonly ILogger<DeepThinkService> _logger;

        public DeepThinkService(
            IHttpClientFactory httpClientFactory,
            ILogger<DeepThinkService> logger)
        {
            _httpClient = httpClientFactory.CreateClient("Ollama");
            _logger = logger;
        }

        public async Task<AssistantResponse> ProcessDeepThinkAsync(ConversationState conversation, string userMessage)
        {
            try
            {
                _logger.LogInformation("🚀 Deep Think started for: {Message}", userMessage);

                //                var prompt = $@"
                //USER REQUEST: {userMessage}

                //CRITICAL INSTRUCTIONS:
                //1. If user asks for SPECIFIC movies (Harry Potter, James Bond, etc) - recommend ACTUAL movies from that series
                //2. Recommend ONLY relevant movies - no random suggestions
                //3. List 5 movies MAX
                //4. Be direct and accurate
                //5. Talk with user on his language. If he speaks russian, you must speak on russian with user 
                //6. If he speaks russian, you must find real correct film's titles on russian. Don't give him wrong titels. FIND CORRECT TITELS TRANSLATIONS:
                //For example:  The Bourne Identity => Идентификация борна, а не  Борна: Идентичность YOU MUST FIND TRANSLATIONS but not translate by our own

                //EXAMPLE:
                //If user says ""Harry Potter"" - recommend: Harry Potter and the Philosopher's Stone, Harry Potter and the Chamber of Secrets, etc.
                //If user says ""James Bond"" - recommend: Casino Royale, Skyfall, Goldfinger, etc.

                //YOUR RESPONSE MUST END WITH:
                //RECOMMENDED_MOVIES:
                //• Movie 1 (Year)
                //• Movie 2 (Year)
                //• Movie 3 (Year)
                //• Movie 4 (Year)
                //• Movie 5 (Year)

                //Your response:";

                var prompt = $@"
USER REQUEST: {userMessage}

CRITICAL INSTRUCTIONS:
1. If user asks for SPECIFIC movies (Harry Potter, James Bond, etc) - recommend ACTUAL movies from that series
2. Recommend ONLY relevant movies - no random suggestions
3. List 5 movies MAX
4. Be direct and accurate

EXAMPLE:
If user says ""Harry Potter"" - recommend: Harry Potter and the Philosopher's Stone, Harry Potter and the Chamber of Secrets, etc.
If user says ""James Bond"" - recommend: Casino Royale, Skyfall, Goldfinger, etc.

YOUR RESPONSE MUST END WITH:
RECOMMENDED_MOVIES:
• Movie 1 (Year)
• Movie 2 (Year)
• Movie 3 (Year)
• Movie 4 (Year)
• Movie 5 (Year)

Your response:";

                var responseText = await CallLlmAsync(prompt, "llama3.1", 0.3); // Низкая температура для точности

                var recommendedMovies = ExtractRecommendedMovies(responseText);

                return new AssistantResponse
                {
                    Response = "🔍 **Deep Think Analysis**\n\n" + responseText,
                    RecommendedMovies = recommendedMovies,
                    ConversationId = conversation.ConversationId,
                    NeedsClarification = false,
                    UsedDeepThink = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Deep Think");

                return new AssistantResponse
                {
                    Response = "🔍 **Deep Think Analysis**\n\nSorry, deep analysis is temporarily unavailable.",
                    ConversationId = conversation.ConversationId,
                    RecommendedMovies = new List<Movie>(),
                    NeedsClarification = false,
                    UsedDeepThink = true
                };
            }
        }

        private List<Movie> ExtractRecommendedMovies(string responseText)
        {
            var movies = new List<Movie>();

            try
            {
                var lines = responseText.Split('\n');
                var inRecommendedSection = false;

                foreach (var line in lines)
                {
                    if (line.Contains("RECOMMENDED_MOVIES:"))
                    {
                        inRecommendedSection = true;
                        continue;
                    }

                    if (inRecommendedSection && (line.Trim().StartsWith("•") || line.Trim().StartsWith("-")))
                    {
                        var match = Regex.Match(line, @"[•\-]\s*(.+?)\s*(?:\((\d{4})\))?$");
                        if (match.Success)
                        {
                            var title = match.Groups[1].Value.Trim();
                            var year = match.Groups[2].Success ? match.Groups[2].Value : "2000";

                            movies.Add(new Movie
                            {
                                Title = title,
                                ReleaseDate = DateTime.Parse($"{year}-01-01"),
                                Overview = "Recommended by AI",
                                VoteAverage = 8.0
                            });

                            _logger.LogInformation("🎬 AI recommended: {Title}", title);
                        }
                    }

                    if (movies.Count >= 5) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting movies");
            }

            return movies.Any() ? movies : GetFallbackMovies();
        }

        private List<Movie> GetFallbackMovies()
        {
            return new List<Movie>
            {
                
            };
        }

        private async Task<string> CallLlmAsync(string prompt, string model = "llama3.1", double temperature = 0.7)
        {
            try
            {
                var request = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false,
                    options = new { temperature = temperature, num_predict = 600 }
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

                return "Analysis failed";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling LLM");
                return "Service unavailable";
            }
        }
    }
}