using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Pgvector;
namespace BackendForLab2AI.Models
{
    public class Movie
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public bool Adult { get; set; }

        public string? BelongsToCollection { get; set; }

        public long Budget { get; set; }

        public string? Genres { get; set; }

        public string? Homepage { get; set; }

        public int MovieId { get; set; } 

        public string? ImdbId { get; set; }

        public string? OriginalLanguage { get; set; }

        [Required]
        public string? OriginalTitle { get; set; }

        public string? Overview { get; set; }

        public double Popularity { get; set; }

        public string? PosterPath { get; set; }

        public string? ProductionCompanies { get; set; }

        public string? ProductionCountries { get; set; }

        public DateTime? ReleaseDate { get; set; }

        public long Revenue { get; set; }

        public int? Runtime { get; set; }

        public string? SpokenLanguages { get; set; }

        public string? Status { get; set; }

        public string? Tagline { get; set; }

        [Required]
        public string? Title { get; set; }

        public bool Video { get; set; }

        public double VoteAverage { get; set; }

        public int VoteCount { get; set; }

        // Эмбеддинги для разных моделей
        public Vector? Embedding { get; set; }        // Для nomic-embed-text
        public Vector? EmbeddingAllMiniLM { get; set; } // Для all-minilm
        public Vector? EmbeddingBgeM3 { get; set; }   // Для bge-m3
    }
}
