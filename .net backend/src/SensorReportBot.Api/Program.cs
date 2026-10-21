using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SensorReportBot.Api.Middleware;
using SensorReportBot.Application;
using SensorReportBot.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog logger
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// Add Application & Infrastructure layer registrations
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
    options.HandshakeTimeout = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<SensorReportBot.Application.Interfaces.IJobProgressNotifier, SensorReportBot.Api.Services.SignalRJobProgressNotifier>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// Configure CORS for local development (with AllowCredentials for WebSockets / SignalR)
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5173", "http://localhost:5174" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactDev", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Global Exception Handling Middleware
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowReactDev");
app.UseAuthorization();

app.MapControllers();
app.MapHub<SensorReportBot.Api.Hubs.ChatHub>("/hubs/chat");
app.MapHealthChecks("/health");

app.MapGet("/api/model-info", (
    Microsoft.Extensions.Options.IOptions<SensorReportBot.Infrastructure.Configuration.LlmOptions> options,
    SensorReportBot.Application.Interfaces.ILayaClient layaClient) =>
{
    var opt = options.Value;
    return Results.Ok(new
    {
        activeModel = opt.Model,
        model = opt.Model,
        provider = "Ollama",
        endpoint = opt.Endpoint,
        temperature = opt.Temperature,
        timeoutSeconds = opt.TimeoutSeconds,
        architecture = "Qwen 2.5",
        pipeline = new
        {
            chatReasoning = opt.Model,
            decisionModel = layaClient.IsEnabled ? "JEV / Laya (In-Process ONNX, Inactive in Chat)" : "None"
        }
    });
});

app.Run();
