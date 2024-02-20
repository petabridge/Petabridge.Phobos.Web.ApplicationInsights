// -----------------------------------------------------------------------
// <copyright file="SerilogBootstrapper.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2021 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Net;
using System.Reflection;
using Akka.Configuration;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Petabridge.Phobos.Web.ApplicationInsights
{
    /// <summary>
    ///     Used to help load our Seq configuration in at application startup.
    ///     https://docs.datalust.co/docs/getting-started-with-docker
    /// </summary>
    public static class SeqBootstrapper
    {
        public const string SEQ_SERVICE_HOST = "SEQ_SERVICE_HOST";
        public const string SEQ_SERVICE_PORT = "SEQ_SERVICE_PORT";

        public const string DefaultSeqUrl = "http://localhost:5341";

        /// <summary>
        ///     Checks to see if the <see cref="SEQ_SERVICE_HOST" /> and <see cref="SEQ_SERVICE_PORT" /> environment variables are
        ///     defined.
        /// </summary>
        public static bool SeqEnabled =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SEQ_SERVICE_HOST)) &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SEQ_SERVICE_PORT));

        public static string LoadSeqUrl()
        {
            if (SeqEnabled)
                return
                    $"http://{Environment.GetEnvironmentVariable(SEQ_SERVICE_HOST)}:{Environment.GetEnvironmentVariable(SEQ_SERVICE_PORT)}";

            // if the environment variables weren't present, return the default Seq url.

            return DefaultSeqUrl;
        }

        public static string GetServiceName()
        {
            var podName = Environment.GetEnvironmentVariable(SerilogBootstrapper.PodNameProperty);

            return !string.IsNullOrEmpty(podName) ? podName : Dns.GetHostName();
        }

        public static string GetEnvironmentName()
        {
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            return !string.IsNullOrEmpty(environmentName) ? environmentName : "Development";
        }
    }

    /// <summary>
    ///     Used to configure and install Serilog for semantic logging for both
    ///     ASP.NET and Akka.NET
    /// </summary>
    public static class SerilogBootstrapper
    {
        public const string ServiceNameProperty = "SERVICE_NAME";
        public const string PodNameProperty = "POD_NAME";
        public const string EnvironmentProperty = "ENVIRONMENT";

        static SerilogBootstrapper()
        {
            var loggerConfiguration = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.WithProperty(PodNameProperty, SeqBootstrapper.GetServiceName())
                .Enrich.WithProperty(EnvironmentProperty, SeqBootstrapper.GetEnvironmentName())
                .Enrich.WithProperty(ServiceNameProperty, Assembly.GetEntryAssembly()!.GetName().Name!)
                .WriteTo.Console(
                    outputTemplate:
                    "[{POD_NAME}][{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}",
                    theme: AnsiConsoleTheme.Literate);

            // Configure Serilog
            Log.Logger = loggerConfiguration.CreateLogger();
        }

        public static IHostBuilder ConfigureSerilogLogging(this IHostBuilder b)
        {
            return b.ConfigureLogging((hostingContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddSerilog();
                logging.AddEventSourceLogger();
            });
        }
    }
}