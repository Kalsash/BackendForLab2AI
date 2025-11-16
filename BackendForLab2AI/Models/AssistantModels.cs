// AssistantModels.cs
namespace BackendForLab2AI.Models
{
    public class ConversationState
    {
        public string ConversationId { get; set; } = Guid.NewGuid().ToString();
        public List<Message> Messages { get; set; } = new();
        public UserPreferences Preferences { get; set; } = new();
        public string Language { get; set; } = "en"; // Добавляем язык беседы, по умолчанию английский
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class UserPreferences
    {
        public List<string> Genres { get; set; } = new();
        public List<string> Moods { get; set; } = new();
        public string? TimePeriod { get; set; }
        public string? LanguagePreference { get; set; }
        public int? DesiredRuntime { get; set; }
        public List<string> PreviouslyLikedMovies { get; set; } = new();
        public List<string> AvoidedMovies { get; set; } = new();
    }

    public class Message
    {
        public string Role { get; set; } = string.Empty; // "user", "assistant", "system"
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class AssistantResponse
    {
        public string Response { get; set; } = string.Empty;
        public List<Movie> RecommendedMovies { get; set; } = new();
        public bool NeedsClarification { get; set; }
        public List<string> ClarificationQuestions { get; set; } = new();
        public string ConversationId { get; set; } = string.Empty;

        public bool UsedDeepThink { get; set; } = false;

    }

    public class AssistantRequest
    {
        public string Message { get; set; } = string.Empty;
        public string? ConversationId { get; set; }
        public bool ResetConversation { get; set; }

        public bool UseDeepThink { get; set; }
    }
}