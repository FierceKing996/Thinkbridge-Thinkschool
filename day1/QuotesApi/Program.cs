using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuotesApi;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Infrastructure (DI, DbContext)
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// 2. Exception middleware returning ProblemDetails
app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "An unexpected error occurred.",
            Status = context.Response.StatusCode,
            Detail = exceptionHandlerPathFeature?.Error.Message
        });
    });
});

// 3. EF Core migrations applied at startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // EnsureCreated can be used for simple SQLite setups, or MigrateAsync if you create migrations
    await dbContext.Database.MigrateAsync(); 
}

// 4. Map Endpoints
app.MapQuoteEndpoints();

app.Run();