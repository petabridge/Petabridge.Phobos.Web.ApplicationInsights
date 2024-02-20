// -----------------------------------------------------------------------
//   <copyright file="Extensions.cs" company="Petabridge, LLC">
//     Copyright (C) 2015-2024 .NET Petabridge, LLC
//   </copyright>
// -----------------------------------------------------------------------

using System;
using System.Linq;
using Akka.Cluster.Hosting;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Petabridge.Phobos.Web.ApplicationInsights;

public static class Extensions
{
    public static AkkaConfigurationBuilder BootstrapFromDocker(this AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IConfiguration>().GetEnvironmentVariables();
        if (options is null)
            return builder;

        var remoteOptions = new RemoteOptions
        {
            PublicHostName = "localhost",
            HostName = "0.0.0.0",
            Port = 4055
        };
        if (options.Ip is not null)
            remoteOptions.PublicHostName = options.Ip;
        if (options.Port is not null)
            remoteOptions.Port = options.Port;

        var clusterOptions = new ClusterOptions
        {
            Roles = new [] {"console"}
        };
        if (options.Seeds is not null)
            clusterOptions.SeedNodes = options.Seeds;
        if (options.Roles is not null)
            clusterOptions.Roles = options.Roles;

        builder
            .WithRemoting(remoteOptions)
            .WithClustering(clusterOptions);

        return builder;
    }
    
    private static NetworkOptions? GetEnvironmentVariables(this IConfiguration configuration)
    {
        var section = configuration.GetSection("Cluster");
        if(!section.GetChildren().Any())
        {
            Console.WriteLine("Skipping environment variable bootstrap. No 'CLUSTER' section found");
            return null;
        }
            
        var options = section.Get<NetworkOptions>();
        if (options is null)
        {
            Console.WriteLine("Skipping environment variable bootstrap. Could not bind IConfiguration to 'ClusterOptions'");
            return null;
        }

        return options;
    }
}