using ShopApi.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddControllers();

var app = builder.Build();
app.MapGet("/health", () => "ok");
app.MapControllers();
app.Run();
