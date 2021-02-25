using System;
using System.IO;
using System.Text.RegularExpressions;
using Bomberjam.Website.Authentication;
using Bomberjam.Website.Database;
using Bomberjam.Website.Jobs;
using Bomberjam.Website.Storage;
using Hangfire;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;

namespace Bomberjam.Website
{
    public class Startup
    {
        private static readonly Regex SqliteDataSourceRegex = new Regex(
            "Data Source=(?<filename>[a-z0-9]+\\.db)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Startup(IConfiguration configuration, IWebHostEnvironment environment)
        {
            this.Configuration = configuration;
            this.Environment = environment;
        }

        private IConfiguration Configuration { get; }
        private IWebHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddResponseCompression(opts =>
            {
                opts.EnableForHttps = true;
            });

            services.AddRouting();

            if (this.Environment.IsDevelopment())
            {
                services.AddControllersWithViews().AddRazorRuntimeCompilation();
            }
            else
            {
                services.AddControllersWithViews();
            }

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/signin";
                    options.LogoutPath = "/signout";
                })
                .AddGitHub(options =>
                {
                    options.ClientId = this.Configuration["GitHub:ClientId"];
                    options.ClientSecret = this.Configuration["GitHub:ClientSecret"];
                    options.Scope.Add("user:email");
                    options.CallbackPath = "/signin-github-callback";
                })
                .AddSecret(this.Configuration["SecretAuth:Secret"]);

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Bomberjam API", Version = "v1" });
            });

            IBotStorage botStorage;
            if (this.Configuration.GetConnectionString("BomberjamStorage") is { Length: > 0 } storageConnStr)
            {
                botStorage = storageConnStr.StartsWith("DefaultEndpointsProtocol", StringComparison.OrdinalIgnoreCase)
                    ? (IBotStorage)new AzureStorageBotStorage(storageConnStr)
                    : new LocalFileBotStorage(storageConnStr);
            }
            else
            {
                botStorage = new LocalFileBotStorage(Path.GetTempPath());
            }

            services.AddSingleton(botStorage);

            var dbConnStr = this.Configuration.GetConnectionString("BomberjamContext");

            services.AddDbContext<BomberjamContext>(options =>
            {
                options.UseSqlite(dbConnStr).EnableSensitiveDataLogging();
            });

            services.AddScoped<IRepository, DatabaseRepository>();

            if (SqliteDataSourceRegex.Match(dbConnStr) is { Success: true } match)
            {
                services.AddHangfire(config => config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseSQLiteStorage(match.Groups["filename"].Value));
            }
            else
            {
                throw new Exception($"Could not extract SQLite database file name from the connection string '{dbConnStr}'");
            }

            // Add the processing server as IHostedService
            services.AddHangfireServer();

            // Add framework services.
            services.AddMvc();

            var zippedBotFileStream = typeof(Startup).Assembly.GetManifestResourceStream("Bomberjam.Website.MyBot.zip");
            if (zippedBotFileStream != null)
            {
                using (zippedBotFileStream)
                using (var zippedBotFileMs = new MemoryStream())
                {
                    zippedBotFileStream.CopyTo(zippedBotFileMs);
                    var zippedBotFileBytes = zippedBotFileMs.ToArray();

                    botStorage.UploadBotSourceCode(Constants.UserAskaiserId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                    botStorage.UploadBotSourceCode(Constants.UserFalgarId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                    botStorage.UploadBotSourceCode(Constants.UserXenureId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                    botStorage.UploadBotSourceCode(Constants.UserMintyId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                    botStorage.UploadBotSourceCode(Constants.UserKalmeraId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                    botStorage.UploadBotSourceCode(Constants.UserPandarfId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                    botStorage.UploadBotSourceCode(Constants.UserMireId, new MemoryStream(zippedBotFileBytes)).GetAwaiter().GetResult();
                }
            }
        }

        public void Configure(IApplicationBuilder app, IRecurringJobManager recurringJobs)
        {
            if ("true".Equals(this.Configuration["EnableAutomaticMigrations"], StringComparison.OrdinalIgnoreCase))
            {
                var scopeFactory = app.ApplicationServices.GetService<IServiceScopeFactory>();
                if (scopeFactory != null)
                {
                    using (var scope = scopeFactory.CreateScope())
                    {
                        scope.ServiceProvider.GetRequiredService<BomberjamContext>().Database.Migrate();
                    }
                }
            }

            app.UseResponseCompression();

            if (this.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Bomberjam API"));
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            // Required to serve files with no extension in the .well-known folder
            var options = new StaticFileOptions
            {
                ServeUnknownFileTypes = true
            };

            app.UseHttpsRedirection();
            app.UseStaticFiles(options);

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapDefaultControllerRoute();
                endpoints.MapHangfireDashboard();
            });

            recurringJobs.AddOrUpdate<MatchmakingJob>("matchmaking", job => job.Run(), Cron.Minutely);
        }
    }
}
