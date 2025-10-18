using BackendForLab2AI.Data;
using BackendForLab2AI.Services;
using Microsoft.EntityFrameworkCore;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});


builder.Services.AddControllers();
builder.Services.AddOpenApi();

//builder.Services.AddDbContext<MovieContext>(options =>
//    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

//builder.Services.AddDbContext<MovieContext>(options =>
//    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddDbContext<MovieContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => npgsqlOptions.UseVector()
    )
);

//// Add HTTP Client for Ollama
builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri("http://localhost:11434/");
    client.Timeout = TimeSpan.FromMinutes(5);
});
// Add HTTP Client for Ollama
//builder.Services.AddHttpClient("Ollama", client =>
//{
//    client.BaseAddress = new Uri("http://host.docker.internal:11434/");
//    client.Timeout = TimeSpan.FromMinutes(5);
//});

builder.Services.AddScoped<IMovieService, MovieService>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowAll");

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

await InitializeDatabaseAsync(app);
app.MapGet("/", () => "OK");

app.Run();

async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    try
    {
        var context = services.GetRequiredService<MovieContext>();

        var retries = 10;
        while (retries > 0)
        {
            try
            {
                Console.WriteLine("Attempting to connect to database...");
                await context.Database.CanConnectAsync();
                break;
            }
            catch (Exception)
            {
                retries--;
                Console.WriteLine($"Database not ready yet. Retries left: {retries}");
                await Task.Delay(5000);
            }
        }
        await context.InitializeDatabaseAsync();
        Console.WriteLine("Database initialized successfully with movie data.");
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while initializing the database.");
    }
}
