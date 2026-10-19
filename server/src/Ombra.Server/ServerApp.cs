using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Blobs;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Retention;
using Ombra.Server.Revocation;
using Ombra.Server.Streaming;
using Ombra.Server.Teams;
using Ombra.Server.Transfers;

namespace Ombra.Server;

public static class ServerApp
{
    /// <summary>Services shared by the HTTP server and ombra-admin.</summary>
    /// <param name="applicationName">Postgres application_name; restore refuses while an "ombra-server" connection exists.</param>
    public static IServiceCollection AddOmbraCore(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        services.AddOptions<OmbraOptions>().Bind(configuration.GetSection(OmbraOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Ombra:ConnectionString is required");
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var csb = new NpgsqlConnectionStringBuilder(sp.GetRequiredService<IOptions<OmbraOptions>>().Value.ConnectionString)
            {
                ApplicationName = applicationName,
            };
            return NpgsqlDataSource.Create(csb.ConnectionString);
        });
        services.AddSingleton<TransferStore>();
        services.AddSingleton<RevocationService>();
        services.AddSingleton<BlobService>();
        services.AddSingleton<RetentionService>();
        services.AddSingleton<Backup.BackupService>();
        // ombra-admin relays an owner-signed equivocation resolution (R2-08) through the same service as the API.
        services.AddSingleton(sp => new ServerIdentity(sp.GetRequiredService<NpgsqlDataSource>(),
            ServerIdentity.ReadServerId(sp.GetRequiredService<IOptions<OmbraOptions>>().Value.ServerIdFile)));
        services.AddSingleton<RosterService>();
        return services;
    }

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOmbraCore(builder.Configuration, "ombra-server");
        services.AddSingleton(sp => TokenKey.Load(sp.GetRequiredService<IOptions<OmbraOptions>>().Value.TokenKeyFile));
        services.AddSingleton<TokenService>();
        services.AddSingleton<DeviceAuth>();
        services.AddSingleton<Envelopes>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<StreamHub>();
        services.AddSingleton<OpRateLimiter>();
        services.AddSingleton<DeliveryService>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<TransferService>();
        services.AddSingleton<TeamService>();
        services.AddSingleton<TeamSignalsService>();
        services.AddSingleton<Devices.DeviceService>();
        services.AddSingleton<Groups.GroupService>();
        services.AddSingleton<Groups.KeyPackageService>();
        services.AddHostedService<StartupInitializer>();
        services.AddHostedService<RetentionHostedService>();
        services.AddSingleton<StreamRevalidator>();
        services.AddHostedService(sp => sp.GetRequiredService<StreamRevalidator>());
        services.AddHostedService<RevocationListener>();
        services.AddRequestGuards();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        builder.WebHost.ConfigureKestrel((ctx, k) =>
        {
            var o = ctx.Configuration.GetSection(OmbraOptions.Section).Get<OmbraOptions>() ?? new OmbraOptions();
            k.AddServerHeader = false;
            // Small by default; RequestGuards raises it per request only for the streaming upload routes.
            k.Limits.MaxRequestBodySize = o.Limits.RequestBodyBytes;
            var uri = new Uri(o.ListenUrl);
            var ip = uri.Host is "0.0.0.0" or "*" ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(uri.Host);
            k.Listen(ip, uri.Port, listen =>
            {
                if (o.Mode != "ip") return; // domain mode: Caddy terminates TLS on the internal network
                // §6.2: the pinned SPKI is the key of this certificate; TLS 1.3 only.
                var cert = X509Certificate2.CreateFromPemFile(o.TlsCertFile!, o.TlsKeyFile!);
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
                    https.SslProtocols = SslProtocols.Tls13;
                });
            });
        });
    }

    /// <summary>Migrations and startup invariants run in <c>StartingAsync</c>, before the server or any job starts.</summary>
    private sealed class StartupInitializer(IServiceProvider sp) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            var o = sp.GetRequiredService<IOptions<OmbraOptions>>().Value;
            var db = sp.GetRequiredService<NpgsqlDataSource>();
            await Migrator.MigrateAsync(db, ct: cancellationToken);
            await ServerIdentity.InitializeAsync(db, sp.GetRequiredService<ServerIdentity>().ServerId, sp.GetRequiredService<TokenKey>());
            Directory.CreateDirectory(o.BlobDir);
            Directory.CreateDirectory(o.TransferDir);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static void Map(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (ApiException e) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = e.Status;
                if (e.RetryAfter is { } ra) ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse(e.Reason, e.Detail, e.RemovalNotices), JsonDefaults.Options);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = e.StatusCode;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse(Reasons.BadRequest, e.Message), JsonDefaults.Options);
            }
            catch (JsonException e) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse(Reasons.BadRequest, e.Message), JsonDefaults.Options);
            }
        });
        if (app.Services.GetRequiredService<IOptions<OmbraOptions>>().Value.Mode == "domain")
        {
            // Domain mode: Kestrel is reachable only through Caddy (published on 127.0.0.1 / the edge network), so the
            // client address for the pre-auth rate limit is Caddy's X-Forwarded-For.
            var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
            fwd.KnownIPNetworks.Clear();
            fwd.KnownProxies.Clear();
            app.UseForwardedHeaders(fwd);
        }
        app.UseBodyLimits();
        app.UseRateLimiter();
        Endpoints.Map(app);
    }
}
