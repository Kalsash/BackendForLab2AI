using BackendForLab2AI.Data;
using BackendForLab2AI.DTOs;
using BackendForLab2AI.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace BackendForLab2AI.Services
{
    public class MovieService : IMovieService
    {
        private readonly MovieContext _context;

        public MovieService(MovieContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Movie>> GetAllMoviesAsync()
        {
            return await _context.Movies.ToListAsync();
        }

        public async Task<Movie?> GetMovieByIdAsync(int id)
        {
            return await _context.Movies.FirstOrDefaultAsync(m => m.Id == id);
        }

        public async Task<Movie?> GetMovieByTitleAsync(string title)
        {
            return await _context.Movies
                .FirstOrDefaultAsync(m => m.Title != null && m.Title.ToLower() == title.ToLower());
        }

        public async Task<Movie> CreateMovieAsync(CreateMovieDto movieDto)
        {
            var movie = new Movie
            {
                Adult = movieDto.Adult,
                BelongsToCollection = movieDto.BelongsToCollection,
                Budget = movieDto.Budget,
                Genres = movieDto.Genres,
                Homepage = movieDto.Homepage,
                MovieId = movieDto.MovieId,
                ImdbId = movieDto.ImdbId,
                OriginalLanguage = movieDto.OriginalLanguage,
                OriginalTitle = movieDto.OriginalTitle,
                Overview = movieDto.Overview,
                Popularity = movieDto.Popularity,
                PosterPath = movieDto.PosterPath,
                ProductionCompanies = movieDto.ProductionCompanies,
                ProductionCountries = movieDto.ProductionCountries,
                ReleaseDate = movieDto.ReleaseDate,
                Revenue = movieDto.Revenue,
                Runtime = movieDto.Runtime,
                SpokenLanguages = movieDto.SpokenLanguages,
                Status = movieDto.Status,
                Tagline = movieDto.Tagline,
                Title = movieDto.Title,
                Video = movieDto.Video,
                VoteAverage = movieDto.VoteAverage,
                VoteCount = movieDto.VoteCount
            };

            _context.Movies.Add(movie);
            await _context.SaveChangesAsync();
            return movie;
        }

        public async Task<Movie?> UpdateMovieAsync(int id, UpdateMovieDto movieDto)
        {
            var movie = await _context.Movies.FindAsync(id);
            if (movie == null)
                return null;

            movie.Adult = movieDto.Adult;
            movie.BelongsToCollection = movieDto.BelongsToCollection;
            movie.Budget = movieDto.Budget;
            movie.Genres = movieDto.Genres;
            movie.Homepage = movieDto.Homepage;
            movie.MovieId = movieDto.MovieId;
            movie.ImdbId = movieDto.ImdbId;
            movie.OriginalLanguage = movieDto.OriginalLanguage;
            movie.OriginalTitle = movieDto.OriginalTitle;
            movie.Overview = movieDto.Overview;
            movie.Popularity = movieDto.Popularity;
            movie.PosterPath = movieDto.PosterPath;
            movie.ProductionCompanies = movieDto.ProductionCompanies;
            movie.ProductionCountries = movieDto.ProductionCountries;
            movie.ReleaseDate = movieDto.ReleaseDate;
            movie.Revenue = movieDto.Revenue;
            movie.Runtime = movieDto.Runtime;
            movie.SpokenLanguages = movieDto.SpokenLanguages;
            movie.Status = movieDto.Status;
            movie.Tagline = movieDto.Tagline;
            movie.Title = movieDto.Title;
            movie.Video = movieDto.Video;
            movie.VoteAverage = movieDto.VoteAverage;
            movie.VoteCount = movieDto.VoteCount;

            await _context.SaveChangesAsync();
            return movie;
        }

        public async Task<bool> DeleteMovieAsync(int id)
        {
            var movie = await _context.Movies.FindAsync(id);
            if (movie == null)
                return false;

            _context.Movies.Remove(movie);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> InitializeDatabaseAsync()
        {
            if (await _context.Movies.AnyAsync())
                return false; 

            var jsonData = await File.ReadAllTextAsync("MoviesData.json");
            var movies = JsonSerializer.Deserialize<List<Movie>>(jsonData, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (movies != null)
            {
                await _context.Movies.AddRangeAsync(movies);
                await _context.SaveChangesAsync();
            }

            return true;
        }
    }
}
