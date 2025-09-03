var builder = WebApplication.CreateBuilder(args);

// Configure URLs BEFORE building the app
if (builder.Environment.IsDevelopment())
{
    // For local development, use available ports
    builder.WebHost.UseUrls("http://localhost:5500", "https://localhost:7500");
}
else
{
    // For production (Railway), use the PORT environment variable
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS - More specific configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
    // Alternative: More restrictive CORS for production
    options.AddPolicy("Development", policy =>
    {
        policy.WithOrigins("https://localhost:7184", "http://localhost:5000", "http://localhost:3000")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials(); // Required for WebSocket connections
    });
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    Console.WriteLine($"Request: {context.Request.Method} {context.Request.Path}");
    await next();
    Console.WriteLine($"Response: {context.Response.StatusCode}");
});

app.UseSwagger();
app.UseSwaggerUI();

// IMPORTANT: Order matters! CORS must be before routing and authorization
app.UseCors("AllowAll");

// Add WebSocket support BEFORE UseRouting
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
});

//app.UseHttpsRedirection();
app.UseRouting(); // This should be after CORS and WebSockets
app.UseAuthorization();

// Map endpoints AFTER UseRouting and UseAuthorization
app.MapControllers();

// Add health endpoints AFTER UseRouting
app.MapGet("/", () => "API is running!");
app.MapGet("/health", () => {
    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow,
        environment = app.Environment.EnvironmentName
    });
});
app.Run();