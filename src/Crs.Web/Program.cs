using System.Text.Json;
using System.Text.Json.Serialization;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Crs.Web;
using Crs.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// Add Blazored LocalStorage for token persistence
builder.Services.AddBlazoredLocalStorage(config =>
{
    config.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    config.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    config.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Register HttpClient with base address from configuration
var apiBaseUrl = builder.Configuration.GetValue<string>("ApiBaseUrl") 
    ?? "https://localhost:5001";
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

// Register application services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<DevelopmentDataStore>();
builder.Services.AddScoped<SourceService>();
builder.Services.AddScoped<FeedService>();
builder.Services.AddScoped<PreferencesService>();
builder.Services.AddScoped<XFeedService>();

await builder.Build().RunAsync();
