
using AiService.Interfaces;
using AiService.Models;
using AiService.Services;
using DevExpress.Entity.Model.Metadata;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NPOI.XWPF.UserModel;
using Polly;
using Polly.Extensions.Http;

using Serilog;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace AiService
{
    public class Program
    {
        
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);


            // Log connection strings Failing early check
            var sqliteConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(sqliteConnectionString))
            {
                Log.Fatal("SQLite connection string 'DefaultConnection' is null or empty in appsettings.json.");
                throw new ArgumentException("SQLite connection string is null or empty.");
            }

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowLocalhost", policy =>
                {
                    policy.WithOrigins("http://localhost:22823") //Origin you want to allow
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();

                });
            });


            builder.Services.AddControllers();
            builder.Services.AddSignalR();
            builder.Services.AddHttpClient<OpenAIService>();
            // Embedding service
            builder.Services.AddMemoryCache();

            builder.Services.AddLogging(logging => logging.AddSerilog());
            var oracleConnectionString = builder.Configuration.GetConnectionString("OracleDb");

            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            #region SQLLITE
            // Add DbContext for SQLite
            //builder.Services.AddDbContext<ApplicationDbContext>(options =>
            //    options.UseSqlite(sqliteConnectionString)
            //           .EnableSensitiveDataLogging(builder.Environment.IsDevelopment())); 
            #endregion


            builder.Services.AddScoped<OpenAIService>();

            builder.Services.AddScoped<EmbeddingService>();




     


            builder.Services.AddDbContext<MyDbContext>(options =>
            {
                options.UseOracle(builder.Configuration.GetConnectionString("OracleDb"));
                // Or UseOracle, UseMySql, etc., depending on your DB
            });



            builder.Services.AddScoped<GPTService>();

            //Semantic Search Service

            //builder.Services.AddScoped<EnhancedSemanticChunker>();
            builder.Services.AddScoped<ImprovedSemanticChunker>();
            builder.Services.AddScoped<AdvancedSemanticChunker>();

            //builder.Services.AddScoped<ChunkingOptions>();
            builder.Services.AddScoped<Chunk_NEW>();

            builder.Services.AddScoped<ChunkingService>();

            // Add this after your DbContext registrations

            // في Program.cs أو Startup.cs
            builder.Services.AddHttpClient("JinaClient", client =>
            {
                client.BaseAddress = new Uri("https://api.jina.ai/v1/");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", "jina_46ee206d6ad7419aa362f17ffcc9727atVXQHOIuZceP1egMS_bRpL6-vvXS");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

            //#region َQwen3-.6B Local
            //builder.Services.AddHttpClient("LocalAIClient", client =>
            //    {
            //        client.BaseAddress = new Uri("http://127.0.0.1:8000/");
            //        client.Timeout = TimeSpan.FromMinutes(10);
            //        client.DefaultRequestHeaders.Add("Accept", "application/json");
            //    });
            //#endregion

            #region َQwen3-.6B Server
            builder.Services.AddHttpClient("LocalAIClient", client =>
            {
                client.BaseAddress = new Uri("http://10.21.2.143:8000/");
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });
            #endregion




            var app = builder.Build();

            app.UseCors("AllowLocalhost");
            app.MapHub<ChatHub>("/chathub");

            //Sedding Data In ApplicationDbcontext

            #region EMBED DATA TO QDRANT
            //using (var scope = app.Services.CreateScope())
            //{
            //    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();


            //    var embeddingService = scope.ServiceProvider.GetRequiredService<EmbeddingService>();
            //    await embeddingService.IndexAllDBAsync(); // Generate and store embeddings
            //                                              //} 
            //}                             //}
            #endregion



            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
                {
                    app.UseSwagger();
                    app.UseSwaggerUI();
                }



                app.UseHttpsRedirection();
                app.UseStatusCodePages(async context =>
                {
                    context.HttpContext.Response.ContentType = "application/json";
                    var statusCode = context.HttpContext.Response.StatusCode;
                    await context.HttpContext.Response.WriteAsync(
                        JsonConvert.SerializeObject(new { error = $"HTTP {statusCode}" })
                    );
                });

                app.UseAuthorization();


                app.MapControllers();

                app.Run();
            }
        }
    }

