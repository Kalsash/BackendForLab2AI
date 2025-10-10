using BackendForLab2AI.Models;

namespace BackendForLab2AI.Services
{
    public interface IEmbeddingService
    {
        Task<List<float>> GetEmbeddingAsync(string text, string model = "nomic-embed-text");
        Task<Dictionary<int, List<float>>> GenerateAllMovieEmbeddingsAsync(string model = "nomic-embed-text");
        Task<List<MovieRecommendation>> FindSimilarMoviesAsync(string query, int topK = 10,
            string model = "nomic-embed-text", string distanceMetric = "cosine");
        Task<List<MovieRecommendation>> FindSimilarMoviesByTitleAsync(string movieTitle, int topK = 10,
            string model = "nomic-embed-text", string distanceMetric = "cosine");
        Task<List<MovieRecommendation>> CompareEmbeddingModelsAsync(string query);
        Task<List<MovieRecommendation>> CompareDistanceMetricsAsync(string query, string model = "nomic-embed-text");
    }
}
