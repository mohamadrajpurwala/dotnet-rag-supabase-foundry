using Azure;
using Azure.AI.OpenAI;
using DocumentApp.Web.Components;
using DocumentApp.Web.DocChunker;
using DocumentApp.Web.Services;
using Microsoft.Extensions.AI;
using Npgsql;

namespace DocumentApp.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddSingleton(_ =>
            {
                var url = builder.Configuration["Supabase:Url"]!;
                var key = builder.Configuration["Supabase:ServiceKey"]!;
                var client = new Supabase.Client(url, key, new Supabase.SupabaseOptions
                {
                    AutoRefreshToken = false,
                    AutoConnectRealtime = false
                });
                //client.InitializeAsync().GetAwaiter().GetResult();
                return client;
            });

            builder.Services.AddSingleton(_ =>
            {
                var ds = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("Supabase"));
                ds.UseVector();               // registers the pgvector type mapping
                return ds.Build();
            });

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddDocumentChunking();

            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            {
                var endpoint = builder.Configuration["EmbeddingOpenAI:Endpoint"];
                var apiKey = builder.Configuration["EmbeddingOpenAI:ApiKey"];
                var deployment = builder.Configuration["EmbeddingOpenAI:Deployment"];

                var azureClient = new AzureOpenAIClient(
                    new Uri(endpoint),
                    new AzureKeyCredential(apiKey)
                );

                return azureClient
                    .GetEmbeddingClient(deployment)
                    .AsIEmbeddingGenerator()
                    .AsBuilder()
                    .UseLogging()
                    .Build(sp);
            });

            builder.Services.AddSingleton<IChatClient>(sp =>
            {
                var endpoint = builder.Configuration["ChatOpenAI:Endpoint"]!;
                var apiKey = builder.Configuration["ChatOpenAI:ApiKey"]!;
                var deployment = builder.Configuration["ChatOpenAI:Deployment"]!;

                var azureClient = new AzureOpenAIClient(
                    new Uri(endpoint),
                    new AzureKeyCredential(apiKey)
                );

                return azureClient
                    .GetChatClient(deployment)
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .UseLogging()
                    .Build(sp);
            });

            builder.Services.AddScoped<IngestionService>();
            builder.Services.AddScoped<ChatService>();

            var app = builder.Build();

            app.UseExceptionHandler("/Error");
            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
