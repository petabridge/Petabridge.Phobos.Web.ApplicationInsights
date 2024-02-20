// -----------------------------------------------------------------------
// <copyright file="Startup.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2021 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Reflection;
using Akka.Actor;
using Akka.Cluster.Hosting;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Logger.Serilog;
using Akka.Routing;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using Phobos.Actor;
using Phobos.Hosting;

namespace Petabridge.Phobos.Web.ApplicationInsights
{
    public class Startup
    {
        private const string OTelSourceName = "Petabridge.Phobos.Web.AppInsight";
        
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            var appInsightsConnectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
            if (appInsightsConnectionString is null)
                throw new Exception("Application Insights connection string was not found in environment variables");
            
            var resource = ResourceBuilder.CreateDefault()
                .AddService(Assembly.GetEntryAssembly()!.GetName().Name!, serviceInstanceId: $"{Dns.GetHostName()}");

            // enables OpenTelemetry for ASP.NET Core
            services
                .AddOpenTelemetry()
                .WithTracing(builder =>
                {
                    builder
                        .SetResourceBuilder(resource)
                        .AddPhobosInstrumentation()
                        .AddSource(OTelSourceName)
                        .AddHttpClientInstrumentation()
                        .AddAspNetCoreInstrumentation()
                        .AddAzureMonitorTraceExporter();
                })
                .WithMetrics(builder =>
                {
                    builder
                        .SetResourceBuilder(resource)
                        .AddPhobosInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddAspNetCoreInstrumentation()
                        .AddAzureMonitorMetricExporter();
                })
                .UseAzureMonitor();

            // sets up Akka.NET
            ConfigureAkka(services);
        }

        public static void ConfigureAkka(IServiceCollection services)
        {
            services.AddAkka("ClusterSys", (builder, provider) =>
            {
                // use our legacy app.conf file
                var config = ConfigurationFactory.ParseString(File.ReadAllText("app.conf"));

                builder
                    .BootstrapFromDocker(provider)
                    .AddHocon(config, HoconAddMode.Prepend)
                    .ConfigureLoggers(loggerBuilder =>
                    {
                        loggerBuilder.ClearLoggers();
                        loggerBuilder.AddLogger<SerilogLogger>();
                    })
                    .WithPhobos(AkkaRunMode.AkkaCluster) // enable Phobos
                    .StartActors((system, registry) =>
                    {
                        var consoleActor = system.ActorOf(Props.Create(() => new ConsoleActor()), "console");
                        var routerActor = system.ActorOf(Props.Empty.WithRouter(FromConfig.Instance), "echo");
                        var routerForwarderActor =
                            system.ActorOf(Props.Create(() => new RouterForwarderActor(routerActor)), "fwd");
                        registry.TryRegister<RouterForwarderActor>(routerForwarderActor);
                    })
                    // start https://cmd.petabridge.com/ for diagnostics
                    .AddPetabridgeCmd(
                        new PetabridgeCmdOptions
                        {
                            // default IP address used to listen for incoming petabridge.cmd client connections
                            // should be a safe default as it listens on "all network interfaces".
                            Host = "0.0.0.0",
                            // default port number used to listen for incoming petabridge.cmd client connections
                            Port = 9110
                        }, 
                        pbmConfig =>
                        {
                            pbmConfig.RegisterCommandPalette(ClusterCommands.Instance);
                            pbmConfig.RegisterCommandPalette(new RemoteCommands());
                        });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment()) app.UseDeveloperExceptionPage();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                var tracer = endpoints.ServiceProvider
                    .GetRequiredService<TracerProvider>()
                    .GetTracer(OTelSourceName);
                
                var actors = endpoints.ServiceProvider.GetRequiredService<ActorRegistry>();
                endpoints.MapGet("/", async context =>
                {
                    // fetch actor references from the registry
                    var routerForwarderActor = actors.Get<RouterForwarderActor>();
                    using (var s = tracer.StartActiveSpan("Cluster.Ask"))
                    {
                        // router actor will deliver message randomly to someone in cluster
                        var resp = await routerForwarderActor.Ask<string>($"hit from {context.TraceIdentifier}",
                            TimeSpan.FromSeconds(5));
                        await context.Response.WriteAsync(resp);
                    }
                });
            });
        }
    }
}