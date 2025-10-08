using BackendForLab2AI.DTOs;
using BackendForLab2AI.Models;
using BackendForLab2AI.Services;
using Microsoft.AspNetCore.Mvc;

namespace BackendForLab2AI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MoviesController : ControllerBase
    {
        private readonly IMovieService _movieService;

        public MoviesController(IMovieService movieService)
        {
            _movieService = movieService;
        }

        // GET: api/movies
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Movie>>> GetMovies()
        {
            var movies = await _movieService.GetAllMoviesAsync();
            return Ok(movies);
        }

        // GET: api/movies/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Movie>> GetMovie(int id)
        {
            var movie = await _movieService.GetMovieByIdAsync(id);
            if (movie == null)
            {
                return NotFound();
            }
            return Ok(movie);
        }

        // GET: api/movies/title/{title}
        [HttpGet("title/{title}")]
        public async Task<ActionResult<Movie>> GetMovieByTitle(string title)
        {
            var movie = await _movieService.GetMovieByTitleAsync(title);
            if (movie == null)
            {
                return NotFound();
            }
            return Ok(movie);
        }

        // POST: api/movies
        [HttpPost]
        public async Task<ActionResult<Movie>> CreateMovie(CreateMovieDto movieDto)
        {
            var movie = await _movieService.CreateMovieAsync(movieDto);
            return CreatedAtAction(nameof(GetMovie), new { id = movie.Id }, movie);
        }

        // PUT: api/movies/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateMovie(int id, UpdateMovieDto movieDto)
        {
            var movie = await _movieService.UpdateMovieAsync(id, movieDto);
            if (movie == null)
            {
                return NotFound();
            }
            return NoContent();
        }

        // DELETE: api/movies/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMovie(int id)
        {
            var result = await _movieService.DeleteMovieAsync(id);
            if (!result)
            {
                return NotFound();
            }
            return NoContent();
        }

        // POST: api/movies/initialize
        [HttpPost("initialize")]
        public async Task<IActionResult> InitializeDatabase()
        {
            var result = await _movieService.InitializeDatabaseAsync();
            if (!result)
            {
                return BadRequest("Database already initialized");
            }
            return Ok("Database initialized with sample data");
        }
    }
}
