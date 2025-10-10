using System.Text.Json.Serialization;

namespace BackendForLab2AI.Models
{
    public class EmbeddingRequest
    {
        public string Model { get; set; } = "nomic-embed-text";
        public string Prompt { get; set; } = "";
    }

    public class EmbeddingResponse
    {
        public string Model { get; set; } = "";

        [JsonPropertyName("embedding")] 
        public List<float> Embedding { get; set; } = new List<float>();
    }

    public class MovieRecommendation
    {
        public Movie Movie { get; set; } = null!;
        public double SimilarityScore { get; set; }
        public string DistanceMetric { get; set; } = "";
    }

    public class RecommendationRequest
    {
        public string? MovieTitle { get; set; }
        public string? Description { get; set; }
        public int TopK { get; set; } = 10;
        public string Model { get; set; } = "nomic-embed-text";
        public string DistanceMetric { get; set; } = "cosine";
    }
}
