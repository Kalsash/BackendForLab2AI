using Microsoft.AspNetCore.Mvc;
using BackendForLab2AI.Models;
using BackendForLab2AI.Services;
namespace BackendForLab2AI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RecommendationsController : ControllerBase
    {
        private readonly IEmbeddingService _embeddingService;
        private readonly ILogger<RecommendationsController> _logger;

        public RecommendationsController(IEmbeddingService embeddingService, ILogger<RecommendationsController> logger)
        {
            _embeddingService = embeddingService;
            _logger = logger;
        }

        [HttpPost("similar")]
        public async Task<ActionResult<List<MovieRecommendation>>> GetSimilarMovies([FromBody] RecommendationRequest request)
        {
            try
            {
                List<MovieRecommendation> recommendations;

                if (!string.IsNullOrEmpty(request.MovieTitle))
                {
                    recommendations = await _embeddingService.FindSimilarMoviesByTitleAsync(
                        request.MovieTitle, request.TopK, request.Model, request.DistanceMetric);
                }
                else if (!string.IsNullOrEmpty(request.Description))
                {
                    recommendations = await _embeddingService.FindSimilarMoviesAsync(
                        request.Description, request.TopK, request.Model, request.DistanceMetric);
                }
                else
                {
                    return BadRequest("Either MovieTitle or Description must be provided");
                }

                return Ok(recommendations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting similar movies");
                return StatusCode(500, "Internal server error");
            }
        }


        [HttpGet("test-embedding")]
        public async Task<ActionResult<List<float>>> TestEmbedding([FromQuery] string text = "test movie")
        {
            try
            {
                var embedding = await _embeddingService.GetEmbeddingAsync(text);
                return Ok(new { dimension = embedding.Count, embedding });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing embedding");
                return StatusCode(500, "Make sure Ollama is running on http://localhost:11434");
            }
        }

        [HttpPost("precompute")]
        public async Task<ActionResult> PrecomputeEmbeddings([FromQuery] string model = "nomic-embed-text")
        {
            try
            {
                var embeddings = await _embeddingService.GenerateAllMovieEmbeddingsAsync(model);
                return Ok(new
                {
                    message = $"Computed embeddings for {embeddings.Count} movies",
                    model = model
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error precomputing embeddings");
                return StatusCode(500, "Error precomputing embeddings");
            }
        }

        // endpoints для управления кэшем
        [HttpGet("available-models")]
        public async Task<ActionResult<List<string>>> GetAvailableModels()
        {
            try
            {
                var models = await _embeddingService.GetAvailableModelsAsync();
                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available models");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpDelete("cache")]
        public async Task<ActionResult> DeleteEmbeddingsCache([FromQuery] string model = null)
        {
            try
            {
                var result = await _embeddingService.DeleteEmbeddingsCacheAsync(model);

                if (result)
                {
                    return Ok(new { message = model == null ? "All caches deleted" : $"Cache for model {model} deleted" });
                }
                else
                {
                    return NotFound(new { message = "Cache not found" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting embeddings cache");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("cache-info")]
        public async Task<ActionResult> GetCacheInfo()
        {
            try
            {
                var models = await _embeddingService.GetAvailableModelsAsync();
                var cacheInfo = new
                {
                    AvailableModels = models,
                    CacheDirectory = Path.Combine(Directory.GetCurrentDirectory(), "EmbeddingsCache")
                };

                return Ok(cacheInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cache info");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}