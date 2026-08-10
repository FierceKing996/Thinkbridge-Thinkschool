using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Refactor_controller.Data;
using Refactor_controller.Exceptions;
using Refactor_controller.Models;
using Refactor_controller.Repositories;
using Refactor_controller.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=orders.db"));

builder.Services.AddScoped<ICustomerRepository, EfCustomerRepository>();
builder.Services.AddScoped<IProductRepository, EfProductRepository>();
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (!db.Customers.Any())
    {
        var addr = new Address { City = "Springfield", Line1 = "1 Main St" };
        db.Addresses.Add(addr);
        db.Customers.Add(new Customer { Name = "Alice", Email = "alice@example.com", Address = addr });
        db.Customers.Add(new Customer { Name = "Bob", Email = "bob@example.com" }); // no address on file, on purpose

        var electronics = new Category { Name = "Electronics" };
        db.Categories.Add(electronics);
        db.Products.Add(new Product { Name = "Widget", Price = 19.99m, StockQuantity = 50, Category = electronics });
        db.Products.Add(new Product { Name = "Gadget", Price = 49.99m, StockQuantity = 5 }); // no category, on purpose

        db.SaveChanges();
    }
}

// Global exception handling: domain exceptions map to meaningful status codes,
// anything else becomes a generic 500 ProblemDetails response.
app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var error = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;

        var (status, title) = error switch
        {
            CustomerNotFoundException or OrderNotFoundException => (StatusCodes.Status404NotFound, error.Message),
            NoOrderableItemsException => (StatusCodes.Status422UnprocessableEntity, error.Message),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = title,
            Status = status,
            Detail = status == StatusCodes.Status500InternalServerError ? null : error?.Message
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
