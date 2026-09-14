
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
        // Swagger UI embeds this in a JSON string inside a JS string literal, which eats one level
        // of escaping, and a failure there stops the whole Swagger page from rendering. So: no
        // backslashes (a regex /\/api/ became //api, a comment), no double quotes, no // comments,
        // and it is sent as one line (raw newlines made JSON.parse throw).
        private const string SwaggerTtsAutoplayJs = """
            (res) => {
              try {
                const url = String(res.url || '');
                if (!res.ok || url.toLowerCase().indexOf('/api/chat/chatwithrag') < 0) return res;
                const body = res.obj || JSON.parse(res.text || res.data || '{}');
                const text = body && body.message;
                if (!text) return res;

                let bar = document.getElementById('tts-bar');
                if (!bar) {
                  bar = document.createElement('div');
                  bar.id = 'tts-bar';
                  bar.style.cssText = 'position:fixed;left:0;right:0;bottom:0;z-index:9999;display:flex;gap:12px;align-items:center;padding:8px 16px;background:#1b1b1b;color:#fff;font:14px sans-serif';
                  const s = document.createElement('span');
                  s.id = 'tts-status';
                  s.textContent = 'TTS';
                  const a = document.createElement('audio');
                  a.id = 'tts-audio';
                  a.controls = true;
                  a.style.flex = '1';
                  bar.appendChild(s);
                  bar.appendChild(a);
                  document.body.appendChild(bar);
                }
                const status = document.getElementById('tts-status');
                const audio = document.getElementById('tts-audio');
                status.textContent = 'Generating speech…';

                fetch(url.slice(0, url.toLowerCase().indexOf('chatwithrag')) + 'SpeakPrepare', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ text })
                })
                  .then(r => r.ok ? r.json() : r.text().then(t => { throw new Error(t || ('HTTP ' + r.status)); }))
                  .then(j => {
                    audio.src = new URL(j.audioUrl, url).href;
                    status.textContent = 'Playing';
                    return audio.play().catch(e => {
                      console.warn('TTS autoplay blocked', e);
                      status.textContent = 'Press play ▶ (' + e.name + ')';
                    });
                  })
                  .catch(e => { status.textContent = 'TTS failed: ' + e.message; });
                audio.onerror = () => { status.textContent = 'TTS audio failed to load'; };
              } catch (e) { console.error('TTS interceptor', e); }
              return res;
            }
            """;


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

            // Text-to-speech: tts_server.py (Nabra-7M-Distill)
            builder.Services.AddHttpClient<TtsService>(client =>
            {
                client.BaseAddress = new Uri(builder.Configuration["Tts:BaseUrl"] ?? "http://127.0.0.1:8010/");
                client.Timeout = TimeSpan.FromMinutes(2);
            });
            builder.Services.AddSingleton<TtsServerLauncher>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TtsServerLauncher>());

            // Speech-to-text: stt_server.py (whisper-large-v3-turbo-arabic-dialectal) for the chat voice button
            builder.Services.AddHttpClient<SttService>(client =>
            {
                client.BaseAddress = new Uri(builder.Configuration["Stt:BaseUrl"] ?? "http://127.0.0.1:8011/");
                client.Timeout = TimeSpan.FromMinutes(2);
            });
            builder.Services.AddSingleton<SttServerLauncher>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SttServerLauncher>());

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
                    app.UseSwaggerUI(c =>
                    {
                        // After ChatWithRAG answers, send the reply to Speak and play it.
                        // A player bar stays at the bottom for replay, or in case the browser blocks autoplay.
                        c.UseResponseInterceptor(SwaggerTtsAutoplayJs.ReplaceLineEndings(" "));
                    });
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

