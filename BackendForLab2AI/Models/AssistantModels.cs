// AssistantModels.cs
namespace BackendForLab2AI.Models
{
    public class ConversationState
    {
        public string ConversationId { get; set; } = Guid.NewGuid().ToString();
        public List<Message> Messages { get; set; } = new();
        public UserPreferences Preferences { get; set; } = new();
        public string Language { get; set; } = "en";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        private const int MAX_MESSAGE_HISTORY = 50;

        public void AddMessage(Message message)
        {
            Messages.Add(message);

            // Ограничиваем размер истории
            if (Messages.Count > MAX_MESSAGE_HISTORY)
            {
                Messages = Messages.Skip(Messages.Count - MAX_MESSAGE_HISTORY).ToList();
            }

            UpdatedAt = DateTime.UtcNow;
        }

        // Перегрузка для удобства
        public void AddMessage(string role, string content)
        {
            AddMessage(new Message
            {
                Role = role,
                Content = content,
                Timestamp = DateTime.UtcNow
            });
        }
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

        public List<string> DislikedGenres { get; set; } = new List<string>();

        public List<string> MentionedKeywords { get; set; } = new List<string>();

        public List<string> LikedMovies { get; set; } = new List<string>();
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

        public string EmbeddingQuery { get; set; } = string.Empty;

    }

    public class AssistantRequest
    {
        public string Message { get; set; } = string.Empty;
        public string? ConversationId { get; set; }
        public bool ResetConversation { get; set; }

        public bool UseDeepThink { get; set; }
    }
}