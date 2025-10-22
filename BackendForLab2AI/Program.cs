using BackendForLab2AI.Data;
using BackendForLab2AI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;


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


var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
    ?? "http://localhost:11434/";

builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
});

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
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        var context = services.GetRequiredService<MovieContext>();

        var retries = 10;
        //while (retries > 0)
        //{
        //    try
        //    {
        //        logger.LogInformation("Attempting to connect to database...");
        //        await context.Database.CanConnectAsync();
        //        break;
        //    }
        //    catch (Exception)
        //    {
        //        retries--;
        //        logger.LogInformation($"Database not ready yet. Retries left: {retries}");
        //        await Task.Delay(5000);
        //    }
        //}
        await context.InitializeDatabaseAsync();
        logger.LogInformation("Database initialized successfully with movie data.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while initializing the database.");
    }
}
