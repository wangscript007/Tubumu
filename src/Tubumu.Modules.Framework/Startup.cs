﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Hangfire.Redis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using OrchardCore.BackgroundTasks;
using OrchardCore.Modules.Manifest;
using StackExchange.Redis.Extensions.Core;
using StackExchange.Redis.Extensions.Core.Abstractions;
using StackExchange.Redis.Extensions.Core.Configuration;
using StackExchange.Redis.Extensions.Core.Implementations;
using StackExchange.Redis.Extensions.Newtonsoft;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Tubumu.Core.Extensions;
using Tubumu.Hangfire;
using Tubumu.Mappings;
using Tubumu.Modules.Framework.Application.Services;
using Tubumu.Modules.Framework.Authorization;
using Tubumu.Modules.Framework.BackgroundTasks;
using Tubumu.Modules.Framework.Extensions;
using Tubumu.Modules.Framework.Models;
using Tubumu.Modules.Framework.Swagger;
using Tubumu.RabbitMQ;
using Tubumu.SignalR;
using Tubumu.Swagger;
using IHostingEnvironment = Microsoft.AspNetCore.Hosting.IHostingEnvironment;
using StartupBase = OrchardCore.Modules.StartupBase;

namespace Tubumu.Modules.Framework
{
    /// <summary>
    /// Startup
    /// </summary>
    public class Startup : StartupBase
    {
        private readonly IConfiguration _configuration;
        private readonly IHostingEnvironment _environment;
        private readonly ILogger<Startup> _logger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="environment"></param>
        /// <param name="logger"></param>
        public Startup(
            IConfiguration configuration,
            IHostingEnvironment environment,
            ILogger<Startup> logger)
        {
            _configuration = configuration;
            _environment = environment;
            _logger = logger;
        }

        /// <summary>
        /// ConfigureServices
        /// </summary>
        /// <param name="services"></param>
        public override void ConfigureServices(IServiceCollection services)
        {
            // Background Service
            services.AddSingleton<IBackgroundTask, IdleBackgroundTask>();
            services.AddSingleton<IBackgroundTask, DailyBackgroundTask>();

            // StackExchange.Redis.Extensions
            // https://github.com/imperugo/StackExchange.Redis.Extensions
            var redisConfiguration = _configuration.GetSection("RedisSettings").Get<RedisConfiguration>();
            services.AddSingleton(redisConfiguration);
            services.AddSingleton<IRedisCacheClient, RedisCacheClient>();
            services.AddSingleton<IRedisCacheConnectionPoolManager, RedisCacheConnectionPoolManager>();
            services.AddSingleton<IRedisDefaultCacheClient, RedisDefaultCacheClient>();
            services.AddSingleton<ISerializer, NewtonsoftSerializer>();
            var redisKeyPrefix = !redisConfiguration.KeyPrefix.IsNullOrWhiteSpace() ? redisConfiguration.KeyPrefix : _environment.ApplicationName;

            // Cache
            services.AddDistributedRedisCache(options =>
            {
                options.Configuration = "localhost";
                options.InstanceName = redisKeyPrefix + ":";
            });
            services.AddMemoryCache();

            // Cors
            services.AddCors(options => options.AddPolicy("DefaultPolicy",
                builder => builder.WithOrigins("http://localhost:9090", "http://localhost:8080").AllowAnyMethod().AllowAnyHeader().AllowCredentials())
            // builder => builder.AllowAnyOrigin.AllowAnyMethod().AllowAnyHeader().AllowCredentials())
            );

            // Cookie
            services.Configure<CookiePolicyOptions>(options =>
            {
                options.CheckConsentNeeded = context => false; // 需保持为 false, 否则 Web API 不会 Set-Cookie 。
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            // Session
            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(10);
                options.Cookie.Name = ".Tubumu.Session";
                options.Cookie.HttpOnly = true;
            });

            // HTTP Client
            services.AddHttpClient();

            // ApiBehaviorOptions
            services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = context => new OkObjectResult(new ApiResult
                {
                    Code = 400,
                    Message = context.ModelState.FirstErrorMessage()
                });
            });

            // Authentication
            services.AddSingleton<IAuthorizationPolicyProvider, TubumuAuthorizationPolicyProvider>();

            services.AddSingleton<ITokenService, TokenService>();
            var tokenValidationSettings = _configuration.GetSection("TokenValidationSettings").Get<TokenValidationSettings>();
            services.AddSingleton(tokenValidationSettings);
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = tokenValidationSettings.ValidIssuer,
                        ValidateIssuer = true,

                        ValidAudience = tokenValidationSettings.ValidAudience,
                        ValidateAudience = true,

                        IssuerSigningKey = SignatureHelper.GenerateSigningKey(tokenValidationSettings.IssuerSigningKey),
                        ValidateIssuerSigningKey = true,

                        ValidateLifetime = tokenValidationSettings.ValidateLifetime,
                        ClockSkew = TimeSpan.FromSeconds(tokenValidationSettings.ClockSkewSeconds),
                    };

                    // We have to hook the OnMessageReceived event in order to
                    // allow the JWT authentication handler to read the access
                    // token from the query string when a WebSocket or 
                    // Server-Sent Events request comes in.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];

                            // If the request is for our hub...
                            var path = context.HttpContext.Request.Path;
                            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                            {
                                // Read the token out of the query string
                                context.Token = accessToken;
                            }
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            _logger.LogError($"Authentication Failed(OnAuthenticationFailed): {context.Request.Path} Error: {context.Exception}");
                            if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                            {
                                context.Response.Headers.Add("Token-Expired", "true");
                            }
                            return Task.CompletedTask;
                        },
                        OnChallenge = context =>
                        {
                            _logger.LogError($"Authentication Challenge(OnChallenge): {context.Request.Path}");

                            // TODO: (alby)为不同客户端返回不同的内容
                            var result = new ApiResultUrl()
                            {
                                Code = 400,
                                Message = "Authentication Challenge",
                                // TODO: (alby)前端 IsDevelopment 为 true 时不返回 Url
                                Url = _environment.IsProduction() ? tokenValidationSettings.LoginUrl : null,
                            };
                            var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            context.Response.Body.Write(body, 0, body.Length);
                            context.HandleResponse();
                            return Task.CompletedTask;
                        }
                    };
                });

            // JSON Date format
            void JsonSetup(MvcJsonOptions options) => options.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss";
            services.Configure((Action<MvcJsonOptions>)JsonSetup);

            // SignalR
            services.AddSignalR();
            services.Replace(ServiceDescriptor.Singleton(typeof(IUserIdProvider), typeof(NameUserIdProvider)));

            // AutoMapper
            services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());
            AutoMapperInitalizer.Initialize();

            // RabbitMQ
            services.AddSingleton<IConnectionPool, ConnectionPool>();
            services.AddSingleton<IChannelPool, ChannelPool>();

            // Swagger
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1.0", new Info { Title = _environment.ApplicationName + " API", Version = "v1.0" });
                c.AddSecurityDefinition("Bearer", new ApiKeyScheme
                {
                    Description = "权限认证(数据将在请求头中进行传输) 参数结构: \"Authorization: Bearer {token}\"",
                    Name = "Authorization",
                    In = "header",
                    Type = "apiKey"
                });
                c.AddSecurityRequirement(new Dictionary<string, IEnumerable<string>>
                {
                    {"Bearer", new string[] { }},
                });
                c.DescribeAllEnumsAsStrings();
                c.DocumentFilter<HiddenApiDocumentFilter>();
                IncludeXmlCommentsForModules(c);
                c.OrderActionsBy(m => m.ActionDescriptor.DisplayName);
                c.ApplyGrouping();
            });

            // Add Hangfire services.
            //services.AddHangfire(configuration => configuration
            //    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
            //    .UseSimpleAssemblyNameTypeSerializer()
            //    .UseRecommendedSerializerSettings()
            //    .UseSqlServerStorage(_configuration.GetConnectionString("Tubumu"), new SqlServerStorageOptions
            //    {
            //        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            //        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            //        QueuePollInterval = TimeSpan.FromSeconds(15),           // 作业队列轮询间隔。默认值为15秒。
            //        JobExpirationCheckInterval = TimeSpan.FromHours(1),     // 作业到期检查间隔（管理过期记录）。默认值为1小时。
            //        UseRecommendedIsolationLevel = true,
            //        UsePageLocksOnDequeue = true,
            //        DisableGlobalLocks = true
            //    }));
            services.AddHangfire(configuration =>
            {
                // 推荐使用 ConnectionMultiplexer，见：https://github.com/marcoCasamento/Hangfire.Redis.StackExchange 。
                // 但是存在 StackExchange.Redis.StrongName 和 StackExchange.Redis 冲突问题。
                configuration.UseRedisStorage("localhost", new RedisStorageOptions
                {
                    Prefix = $"{redisKeyPrefix}:hangfire:",
                    Db = 9,
                });
            });

            // Add the processing server as IHostedService
            services.AddHangfireServer();

            // Data version
            services.AddSingleton<IDataVersionService, DataVersionService>();
        }

        /// <summary>
        /// Configure
        /// </summary>
        /// <param name="app"></param>
        /// <param name="routes"></param>
        /// <param name="serviceProvider"></param>
        public override void Configure(IApplicationBuilder app, IRouteBuilder routes, IServiceProvider serviceProvider)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });
            app.UseAuthentication();

            // Hangfire
            // Configure hangfire to use the new JobActivator we defined.
            GlobalConfiguration.Configuration.UseActivator(new AspNetCoreJobActivator(serviceProvider));
            app.UseHangfireDashboard();
            app.UseHangfireServer();

            app.UseCookiePolicy();
            app.UseCors("DefaultPolicy");
            app.UseSession();

            // Swagger
            var swaggerIndexAssembly = typeof(HiddenApiDocumentFilter).Assembly;
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1.0/swagger.json", _environment.ApplicationName + " API v1.0");
                c.DefaultModelsExpandDepth(-1);
                c.IndexStream = () => swaggerIndexAssembly.GetManifestResourceStream(swaggerIndexAssembly.GetName().Name + ".Tubumu.SwaggerUI.Index.html");
            });
        }

        private void IncludeXmlCommentsForModules(SwaggerGenOptions swaggerGenOptions)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var assembly = Assembly.Load(new AssemblyName(_environment.ApplicationName));
            var moduleNames = assembly.GetCustomAttributes<ModuleNameAttribute>().Select(m => m.Name);
            moduleNames.ForEach(m =>
            {
                var commentsFileName = m + ".XML";
                var commentsFilePath = Path.Combine(baseDirectory, commentsFileName);
                swaggerGenOptions.IncludeAuthorizationXmlComments(commentsFilePath);
            });
        }
    }
}
