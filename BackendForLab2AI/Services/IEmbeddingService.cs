using BackendForLab2AI.Models;
using Pgvector;


public interface IEmbeddingService
{
    Task<List<float>> GetEmbeddingAsync(string text, string model = "nomic-embed-text");
    Task<Dictionary<int, Vector>> GenerateAllMovieEmbeddingsAsync(string model = "nomic-embed-text");
    Task<List<MovieRecommendation>> FindSimilarMoviesAsync(string query, int topK = 10, string model = "nomic-embed-text", string distanceMetric = "cosine");
    Task<List<MovieRecommendation>> FindSimilarMoviesByTitleAsync(string movieTitle, int topK = 10, string model = "nomic-embed-text", string distanceMetric = "cosine");
    Task<List<MovieRecommendation>> CompareEmbeddingModelsAsync(string query);
    Task<List<MovieRecommendation>> CompareDistanceMetricsAsync(string query, string model = "nomic-embed-text");

    Task<bool> SaveEmbeddingsToFileAsync(string model, Dictionary<int, List<float>> embeddings);
    Task<Dictionary<int, List<float>>> LoadEmbeddingsFromFileAsync(string model);
    Task<List<string>> GetAvailableModelsAsync();
    Task<bool> DeleteEmbeddingsCacheAsync(string model = null);
}