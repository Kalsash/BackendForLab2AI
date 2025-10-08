using BackendForLab2AI.DTOs;
using BackendForLab2AI.Models;

namespace BackendForLab2AI.Services
{
    public interface IMovieService
    {
        Task<IEnumerable<Movie>> GetAllMoviesAsync();
        Task<Movie?> GetMovieByIdAsync(int id);
        Task<Movie?> GetMovieByTitleAsync(string title);
        Task<Movie> CreateMovieAsync(CreateMovieDto movieDto);
        Task<Movie?> UpdateMovieAsync(int id, UpdateMovieDto movieDto);
        Task<bool> DeleteMovieAsync(int id);
        Task<bool> InitializeDatabaseAsync();
    }
}
