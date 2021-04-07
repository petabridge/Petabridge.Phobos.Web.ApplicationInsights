// -----------------------------------------------------------------------
// <copyright file="Startup.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2021 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Akka.Actor;
using Akka.Bootstrap.Docker;
using Akka.Configuration;
using App.Metrics;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTracing;
using OpenTracing.Util;
using Petabridge.Tracing.ApplicationInsights;
using Petabridge.Tracing.ApplicationInsights.Propagation;
using Petabridge.Tracing.ApplicationInsights.Util;
using Phobos.Actor;
using Phobos.Actor.Configuration;
using Phobos.Tracing.Scopes;
using Endpoint = Microsoft.AspNetCore.Http.Endpoint;

namespace Petabridge.Phobos.Web
{
    public class Startup
    {
        /// <summary>
        ///     Environment variables used to toggle App Insights / on off.
        /// </summary>
        public const string AppInsightsInstrumentationKeyVariableName = "APP_INSIGHTS_INSTRUMENTATION_KEY";

        public static bool IsAppInsightsEnabled => string.IsNullOrEmpty(
                System.Environment.GetEnvironmentVariable(AppInsightsInstrumentationKeyVariableName));

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            // enables OpenTracing for ASP.NET Core
            services.AddOpenTracing(o =>
            {
                o.ConfigureAspNetCore(a =>
                {
                    a.Hosting.OperationNameResolver = context => $"{context.Request.Method} {context.Request.Path}";

                    // skip Prometheus HTTP /metrics collection from appearing in our tracing system
                    a.Hosting.IgnorePatterns.Add(x => x.Request.Path.StartsWithSegments(new PathString("/metrics")));
                });
                o.ConfigureGenericDiagnostics(c => { });
            });

            // add the Telemetry configuration to our DI dependencies if App Insights is available
            if (IsAppInsightsEnabled)
            {
                var appKey = System.Environment.GetEnvironmentVariable(AppInsightsInstrumentationKeyVariableName);
                var config = new TelemetryConfiguration(appKey);
                services.AddSingleton<TelemetryConfiguration>(config);
            }


            // sets up App Insights + ASP.NET Core metrics
            ConfigureAppMetrics(services);

            // sets up App Insights tracing
            ConfigureTracing(services);

            // sets up Akka.NET
            ConfigureAkka(services);
        }


        public static void ConfigureAppMetrics(IServiceCollection services)
        {
            services.AddMetricsTrackingMiddleware();
            services.AddMetrics(b =>
            {
                var metrics = b.Configuration.Configure(o =>
                    {
                        o.GlobalTags.Add("host", Dns.GetHostName());
                        o.DefaultContextLabel = "akka.net";
                        o.Enabled = true;
                        o.ReportingEnabled = true;
                    });

                if (IsAppInsightsEnabled)
                {
                    metrics = metrics.Report.ToApplicationInsights(opts =>
                    {
                        opts.InstrumentationKey = System.Environment.GetEnvironmentVariable(AppInsightsInstrumentationKeyVariableName);
                        opts.ItemsAsCustomDimensions = true;
                        opts.DefaultCustomDimensionName = "item";
                    });
                }
                else // report to console if AppInsights isn't enabled
                {
                    metrics = metrics.Report.ToConsole();
                }
                
                metrics.Build();
            });
            services.AddMetricsReportingHostedService();
        }

        public static void ConfigureTracing(IServiceCollection services)
        {
            services.AddSingleton<ITracer>(sp =>
            {
                if (IsAppInsightsEnabled)
                {
                    var telConfig = sp.GetRequiredService<TelemetryConfiguration>();
                    var endpoint = new Tracing.ApplicationInsights.Endpoint(Assembly.GetEntryAssembly()?.GetName().Name, Dns.GetHostName(), null);
                    // name the service after the executing assembly
                    var tracer = new ApplicationInsightsTracer(telConfig, new ActorScopeManager(), new B3Propagator(),
                        new DateTimeOffsetTimeProvider(), endpoint);

                    return tracer;
                }

                // use the GlobalTracer, otherwise
                return GlobalTracer.Instance;
            });
            
        }

        public static void ConfigureAkka(IServiceCollection services)
        {
            services.AddSingleton(sp =>
            {
                var metrics = sp.GetRequiredService<IMetricsRoot>();
                var tracer = sp.GetRequiredService<ITracer>();

                var config = ConfigurationFactory.ParseString(File.ReadAllText("app.conf"))
                    .BootstrapFromDocker()
                    .UseSerilog();

                var phobosSetup = PhobosSetup.Create(new PhobosConfigBuilder()
                        .WithMetrics(m =>
                            m.SetMetricsRoot(metrics)) // binds Phobos to same IMetricsRoot as ASP.NET Core
                        .WithTracing(t => t.SetTracer(tracer))) // binds Phobos to same tracer as ASP.NET Core
                    .WithSetup(BootstrapSetup.Create()
                        .WithConfig(config) // passes in the HOCON for Akka.NET to the ActorSystem
                        .WithActorRefProvider(PhobosProviderSelection
                            .Cluster)); // last line activates Phobos inside Akka.NET

                var sys = ActorSystem.Create("ClusterSys", phobosSetup);

                // create actor "container" and bind it to DI, so it can be used by ASP.NET Core
                return new AkkaActors(sys);
            });

            // this will manage Akka.NET lifecycle
            services.AddHostedService<AkkaService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment()) app.UseDeveloperExceptionPage();

            app.UseRouting();

            // enable App.Metrics routes
            app.UseMetricsAllMiddleware();

            app.UseEndpoints(endpoints =>
            {
                var actors = endpoints.ServiceProvider.GetService<AkkaActors>();
                var tracer = endpoints.ServiceProvider.GetService<ITracer>();
                endpoints.MapGet("/", async context =>
                {
                    using (var s = tracer.BuildSpan("Cluster.Ask").StartActive())
                    {
                        // router actor will deliver message randomly to someone in cluster
                        var resp = await actors.RouterForwarderActor.Ask<string>($"hit from {context.TraceIdentifier}",
                            TimeSpan.FromSeconds(5));
                        await context.Response.WriteAsync(resp);
                    }
                });
            });
        }
    }
}