// Models/AssistantModels.cs
using BackendForLab2AI.Services;
using System.Text.Json.Serialization;

namespace BackendForLab2AI.Models
{
    public class AssistantRequest
    {
        public string? ConversationId { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool UseDeepThink { get; set; }
        public bool ResetConversation { get; set; }
    }

    public class AssistantResponse
    {
        public string Response { get; set; } = string.Empty;
        public List<Movie> RecommendedMovies { get; set; } = new List<Movie>();
        public string ConversationId { get; set; } = string.Empty;
        public bool UsedDeepThink { get; set; }
        public string EmbeddingQuery { get; set; } = string.Empty;
        public string FollowUpQuestions { get; set; } = string.Empty;
        public string ReasoningContext { get; set; } = string.Empty;
        public List<ToolCall>ToolCalls { get; set; } = new List<ToolCall>();

        public bool NeedsClarification { get; set; } = false;
        public List<ToolResult>? ToolResults { get; set; }
    }

    public class ConversationState
    {
        public string ConversationId { get; set; } = string.Empty;
        public List<Message> Messages { get; set; } = new List<Message>();
        public UserPreferences Preferences { get; set; } = new UserPreferences();
        public string LastQuestions { get; set; } = string.Empty;
        public DateTime QuestionsGeneratedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Message
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<ToolCall>? ToolCalls { get; set; }
        public string? ToolCallId { get; set; }
    }

    public class UserPreferences
    {
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Themes { get; set; } = new List<string>();
        public string PreferredMood { get; set; } = string.Empty;
        public List<string> AvoidedGenres { get; set; } = new List<string>();
    }

    // Модели для работы с инструментами
    //public class ToolCall
    //{
    //    [JsonPropertyName("id")]
    //    public string Id { get; set; } = string.Empty;

    //    [JsonPropertyName("type")]
    //    public string Type { get; set; } = "function";

    //    [JsonPropertyName("function")]
    //    public FunctionCall Function { get; set; } = new FunctionCall();
    //}

    public class ToolCall
    {
        public string ToolName { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string ContinueMessage { get; set; } = string.Empty;
    }

    public class ToolResult
    {
        public ToolCall ToolCall { get; set; } = new ToolCall();
        public List<Movie> Movies { get; set; } = new List<Movie>();
        public bool Success { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    public class FunctionCall
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }

    public class ToolResponse
    {
        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; } = string.Empty;

        [JsonPropertyName("output")]
        public string Output { get; set; } = string.Empty;
    }

    // Модели для Ollama API
    public class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "gemma3:1b";

        [JsonPropertyName("messages")]
        public List<OllamaMessage> Messages { get; set; } = new List<OllamaMessage>();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        [JsonPropertyName("tools")]
        public List<ToolDefinition>? Tools { get; set; }
    }

    public class OllamaMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("tool_calls")]
        public List<ToolCall>? ToolCalls { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }
    }

    public class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public FunctionDefinition Function { get; set; } = new FunctionDefinition();
    }

    public class FunctionDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public FunctionParameters Parameters { get; set; } = new FunctionParameters();
    }

    public class FunctionParameters
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, ParameterProperty> Properties { get; set; } = new Dictionary<string, ParameterProperty>();

        [JsonPropertyName("required")]
        public List<string> Required { get; set; } = new List<string>();
    }

    public class ParameterProperty
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class OllamaChatResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public OllamaMessage Message { get; set; } = new OllamaMessage();

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }
}