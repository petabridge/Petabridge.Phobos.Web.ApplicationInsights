// -----------------------------------------------------------------------
//   <copyright file="ClusterOptions.cs" company="Petabridge, LLC">
//     Copyright (C) 2015-2024 .NET Petabridge, LLC
//   </copyright>
// -----------------------------------------------------------------------

namespace Petabridge.Phobos.Web.ApplicationInsights;

public class NetworkOptions
{
    public string[]? Seeds { get; set; }
    public string? Ip { get; set; }
    public int? Port { get; set; }
    public string[]? Roles { get; set; }
}