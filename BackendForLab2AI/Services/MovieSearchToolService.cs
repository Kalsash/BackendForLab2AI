using BackendForLab2AI.Models;
using Microsoft.OpenApi.Services;
using System.Text.Json;

namespace BackendForLab2AI.Services
{
    public interface IMovieSearchToolService
    {
        Task<List<Movie>> SearchMoviesAsync(string query, int limit = 5);
        Task<List<Movie>> FindSimilarMoviesAsync(string query, int limit = 5);
        Task<List<Movie>> SearchByGenreAsync(string genre, int limit = 5);
        Task<List<Movie>> SearchByMoodAsync(string mood, int limit = 5);
        //Task<List<Movie>> SearchHighRatedMoviesAsync(string query, int limit = 5);
    }

    public class MovieSearchToolService : IMovieSearchToolService
    {
        private readonly IEmbeddingService _embeddingService;
        private readonly ILogger<MovieSearchToolService> _logger;

        public MovieSearchToolService(IEmbeddingService embeddingService, ILogger<MovieSearchToolService> logger)
        {
            _embeddingService = embeddingService;
            _logger = logger;
        }

        public async Task<List<Movie>> SearchMoviesAsync(string query, int limit = 5)
        {
            try
            {
                _logger.LogInformation("Searching movies with query: {Query}, limit: {Limit}", query, limit);

                var results = await _embeddingService.FindSimilarMoviesAsync(query, 46000, "bge-m3", "cosine");
                var filteredResults = FilterAndSortMovies(results, limit);

                _logger.LogInformation("Found {Count} quality movies after filtering", filteredResults.Count);

                return filteredResults.Take(limit).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching movies with query: {Query}", query);
                return new List<Movie>();
            }
        }

        public async Task<List<Movie>> FindSimilarMoviesAsync(string query, int limit = 5)
        {
            try
            {
                _logger.LogInformation("Finding similar movies with query: {Query}, limit: {Limit}", query, limit);

                var results = await _embeddingService.FindSimilarMoviesAsync(query, 46000, "bge-m3", "cosine");
                return FilterAndSortMovies(results, limit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding similar movies with query: {Query}", query);
                return new List<Movie>();
            }
        }

        public async Task<List<Movie>> SearchByGenreAsync(string genre, int limit = 5)
        {
            try
            {
                _logger.LogInformation("Searching by genre: {Genre}, limit: {Limit}", genre, limit);

                var genreMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                   // { "comedy", "funny hilarious comedy laugh humor popular" },
                    { "comedy", "Comedy" },
                    { "drama", "drama emotional serious story award winning" },
                    { "action", "action adventure exciting thriller blockbuster" },
                    { "romance", "romance love relationship romantic heartfelt" },
                    { "horror", "horror scary suspense thriller" },
                    { "sci-fi", "sci-fi science fiction futuristic space" },
                    { "fantasy", "fantasy magical adventure epic" },
                    { "thriller", "thriller suspense mystery intense" },
                    { "animation", "animation animated family cartoon" },
                    { "adventure", "adventure journey exploration epic" }
                };

                var searchQuery = genreMappings.ContainsKey(genre.ToLower())
                    ? genreMappings[genre.ToLower()]
                    : genre + "action";

                var results = await _embeddingService.FindSimilarMoviesAsync(searchQuery, 46000, "bge-m3", "cosine");
                return FilterAndSortMovies(results, limit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching by genre: {Genre}", genre);
                return new List<Movie>();
            }
        }

        public async Task<List<Movie>> SearchByMoodAsync(string mood, int limit = 5)
        {
            try
            {
                _logger.LogInformation("Searching by mood: {Mood}, limit: {Limit}", mood, limit);

                var moodQueries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "relaxing", "calm peaceful drama relaxing easy watching" },
                    { "exciting", "action adventure thrilling exciting intense" },
                    { "funny", "comedy humorous lighthearted funny laugh" },
                    { "emotional", "drama romantic heartfelt emotional touching" },
                    { "inspiring", "motivational uplifting inspiring inspiring drama" },
                    { "mysterious", "mystery thriller suspense mysterious" },
                    { "romantic", "romance love relationship romantic date" },
                    { "adventurous", "adventure action journey exploration" },
                    { "scary", "horror scary恐怖 suspense thriller" },
                    { "thoughtful", "drama thoughtful philosophical deep" }
                };

                var query = moodQueries.ContainsKey(mood.ToLower())
                    ? moodQueries[mood.ToLower()]
                    : mood + " mood";

                var results = await _embeddingService.FindSimilarMoviesAsync(query, 46000, "bge-m3", "cosine");
                return FilterAndSortMovies(results, limit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching by mood: {Mood}", mood);
                return new List<Movie>();
            }
        }
        private List<Movie> FilterAndSortMovies(List<MovieRecommendation> results, int limit)
        {
            double Popularity = 20;
            List<Movie> FilteredResults = new List<Movie>();
            while (FilteredResults.Count < limit && Popularity > 0)
            {
                FilteredResults = results.Where(r => r.SimilarityScore >= 0.4)
                    .Select(r => r.Movie)
                    .Where(m => m.Popularity >= Popularity)
                    .Take(limit)
                    .OrderByDescending(m => m.Popularity).ToList();
                Popularity--;
            }
            Popularity = 10;
            if (FilteredResults.Count < limit)
            {
                while (FilteredResults.Count < limit && Popularity > 0)
                {
                    FilteredResults = results.Select(r => r.Movie)
                        .Where(m => m.Popularity >= Popularity)
                        .Take(limit).ToList();
                    Popularity--;
                }
            }


            return FilteredResults;
        }

      
    }
}