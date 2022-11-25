using Account;
using Dalamud.Logging;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.Extensions.Logging;
using Palace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Pal.Client.Net
{
    internal class RemoteApi : IDisposable
    {
#if DEBUG
        private const string _remoteUrlGrpc = "http://localhost:5145";
        private const string _remoteUrlGrpcWeb = remoteUrlGrpc;
#else
        private const string _remoteUrlGrpc = "https://pal.μ.tv";
        private const string _remoteUrlGrpcWeb = "https://pal.μ.tv:8443";
#endif
        private readonly string UserAgent = $"{typeof(RemoteApi).Assembly.GetName().Name?.Replace(" ", "")}/{typeof(RemoteApi).Assembly.GetName().Version?.ToString(2)}";

        private readonly ILoggerFactory _grpcToPluginLogLoggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new GrpcLoggerProvider()).AddFilter("Grpc", LogLevel.Trace));

        private GrpcChannel? _channel;
        private LoginReply? _lastLoginReply;
        private bool _warnedAboutUpgrade = false;

        public Guid? AccountId
        {
            get => Service.Configuration.AccountIds.TryGetValue(ConfigurationRemoteUrl, out Guid accountId) ? accountId : null;
            set
            {
                if (value != null)
                    Service.Configuration.AccountIds[ConfigurationRemoteUrl] = value.Value;
                else
                    Service.Configuration.AccountIds.Remove(ConfigurationRemoteUrl);
            }
        }

        private string ConfigurationRemoteUrl => _remoteUrlGrpc;
        private string RemoteUrl => _remoteUrlGrpcWeb;

        private string PartialAccountId =>
            AccountId?.ToString()?.PadRight(14).Substring(0, 13) ?? "[no account id]";

        private async Task<(bool Success, string Error)> TryConnect(CancellationToken cancellationToken, ILoggerFactory? loggerFactory = null, bool retry = true)
        {
            if (Service.Configuration.Mode != Configuration.EMode.Online)
            {
                PluginLog.Debug("TryConnect: Not Online, not attempting to establish a connection");
                return (false, "You are not online.");
            }

            if (_channel == null || !(_channel.State == ConnectivityState.Ready || _channel.State == ConnectivityState.Idle))
            {
                Dispose();

                PluginLog.Information("TryConnect: Creating new gRPC channel");
                HttpMessageHandler httpHandler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(25),
                    SslOptions = GetSslClientAuthenticationOptions(),
                };
                httpHandler = new GrpcWebHandler
                {
                    InnerHandler = httpHandler,
                    HttpVersion = HttpVersion.Version11,
                };
                _channel = GrpcChannel.ForAddress(RemoteUrl, new GrpcChannelOptions
                {
                    HttpHandler = httpHandler,
                    LoggerFactory = loggerFactory,
                });

                PluginLog.Information($"TryConnect: Connecting to upstream service at {RemoteUrl}");
                await _channel.ConnectAsync(cancellationToken);
            }

            var accountClient = new AccountService.AccountServiceClient(_channel);
            if (AccountId == null)
            {
                PluginLog.Information($"TryConnect: No account information saved for {RemoteUrl}, creating new account");
                var createAccountReply = await accountClient.CreateAccountAsync(new CreateAccountRequest(), headers: UnauthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                if (createAccountReply.Success)
                {
                    AccountId = Guid.Parse(createAccountReply.AccountId);
                    PluginLog.Information($"TryConnect: Account created with id {PartialAccountId}");

                    Service.Configuration.Save();
                }
                else
                {
                    PluginLog.Error($"TryConnect: Account creation failed with error {createAccountReply.Error}");
                    if (createAccountReply.Error == CreateAccountError.UpgradeRequired && !_warnedAboutUpgrade)
                    {
                        Service.Chat.PrintError("[Palace Pal] Your version of Palace Pal is outdated, please update the plugin using the Plugin Installer.");
                        _warnedAboutUpgrade = true;
                    }
                    return (false, $"Could not create account ({createAccountReply.Error}).");
                }
            }

            if (AccountId == null)
            {
                PluginLog.Warning("TryConnect: No account id to login with");
                return (false, "No account-id after account was attempted to be created.");
            }

            if (_lastLoginReply == null || string.IsNullOrEmpty(_lastLoginReply.AuthToken) || _lastLoginReply.ExpiresAt.ToDateTime().ToLocalTime() < DateTime.Now)
            {
                PluginLog.Information($"TryConnect: Logging in with account id {PartialAccountId}");
                _lastLoginReply = await accountClient.LoginAsync(new LoginRequest { AccountId = AccountId?.ToString() }, headers: UnauthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
                if (_lastLoginReply.Success)
                {
                    PluginLog.Information($"TryConnect: Login successful with account id: {PartialAccountId}");
                }
                else
                {
                    PluginLog.Error($"TryConnect: Login failed with error {_lastLoginReply.Error}");
                    if (_lastLoginReply.Error == LoginError.InvalidAccountId)
                    {
                        AccountId = null;
                        Service.Configuration.Save();
                        if (retry)
                        {
                            PluginLog.Information("TryConnect: Attempting connection retry without account id");
                            return await TryConnect(cancellationToken, retry: false);
                        }
                        else
                            return (false, "Invalid account id.");
                    }
                    if (_lastLoginReply.Error == LoginError.UpgradeRequired && !_warnedAboutUpgrade)
                    {
                        Service.Chat.PrintError("[Palace Pal] Your version of Palace Pal is outdated, please update the plugin using the Plugin Installer.");
                        _warnedAboutUpgrade = true;
                    }
                    return (false, $"Could not log in ({_lastLoginReply.Error}).");
                }
            }

            if (_lastLoginReply == null)
            {
                PluginLog.Error("TryConnect: No account available");
                return (false, "No login information available.");
            }

            bool success = !string.IsNullOrEmpty(_lastLoginReply?.AuthToken);
            if (!success)
                return (success, "Login reply did not include auth token.");

            return (success, string.Empty);
        }

        private async Task<bool> Connect(CancellationToken cancellationToken)
        {
            var result = await TryConnect(cancellationToken);
            return result.Success;
        }

        public async Task<string> VerifyConnection(CancellationToken cancellationToken = default)
        {
            _warnedAboutUpgrade = false;

            var connectionResult = await TryConnect(cancellationToken, loggerFactory: _grpcToPluginLogLoggerFactory);
            if (!connectionResult.Success)
                return $"Could not connect to server: {connectionResult.Error}";

            PluginLog.Information("VerifyConnection: Connection established, trying to verify auth token");
            var accountClient = new AccountService.AccountServiceClient(_channel);
            await accountClient.VerifyAsync(new VerifyRequest(), headers: AuthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            return "Connection successful.";
        }

        public async Task<(bool, List<Marker>)> DownloadRemoteMarkers(ushort territoryId, CancellationToken cancellationToken = default)
        {
            if (!await Connect(cancellationToken))
                return (false, new());

            var palaceClient = new PalaceService.PalaceServiceClient(_channel);
            var downloadReply = await palaceClient.DownloadFloorsAsync(new DownloadFloorsRequest { TerritoryType = territoryId }, headers: AuthorizedHeaders(), cancellationToken: cancellationToken);
            return (downloadReply.Success, downloadReply.Objects.Select(o => CreateMarkerFromNetworkObject(o)).ToList());
        }

        public async Task<(bool, List<Marker>)> UploadMarker(ushort territoryType, IList<Marker> markers, CancellationToken cancellationToken = default)
        {
            if (markers.Count == 0)
                return (true, new());

            if (!await Connect(cancellationToken))
                return (false, new());

            var palaceClient = new PalaceService.PalaceServiceClient(_channel);
            var uploadRequest = new UploadFloorsRequest
            {
                TerritoryType = territoryType,
            };
            uploadRequest.Objects.AddRange(markers.Select(m => new PalaceObject
            {
                Type = (ObjectType)m.Type,
                X = m.Position.X,
                Y = m.Position.Y,
                Z = m.Position.Z
            }));
            var uploadReply = await palaceClient.UploadFloorsAsync(uploadRequest, headers: AuthorizedHeaders(), cancellationToken: cancellationToken);
            return (uploadReply.Success, uploadReply.Objects.Select(o => CreateMarkerFromNetworkObject(o)).ToList());
        }

        public async Task<bool> MarkAsSeen(ushort territoryType, IList<Marker> markers, CancellationToken cancellationToken = default)
        {
            if (markers.Count == 0)
                return true;

            if (!await Connect(cancellationToken))
                return false;

            var palaceClient = new PalaceService.PalaceServiceClient(_channel);
            var seenRequest = new MarkObjectsSeenRequest { TerritoryType = territoryType };
            foreach (var marker in markers)
                seenRequest.NetworkIds.Add(marker.NetworkId.ToString());

            var seenReply = await palaceClient.MarkObjectsSeenAsync(seenRequest, headers: AuthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            return seenReply.Success;
        }

        private Marker CreateMarkerFromNetworkObject(PalaceObject obj) =>
            new Marker((Marker.EType)obj.Type, new Vector3(obj.X, obj.Y, obj.Z), Guid.Parse(obj.NetworkId));

        public async Task<(bool, List<FloorStatistics>)> FetchStatistics(CancellationToken cancellationToken = default)
        {
            if (!await Connect(cancellationToken))
                return new(false, new List<FloorStatistics>());

            var palaceClient = new PalaceService.PalaceServiceClient(_channel);
            var statisticsReply = await palaceClient.FetchStatisticsAsync(new StatisticsRequest(), headers: AuthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: cancellationToken);
            return (statisticsReply.Success, statisticsReply.FloorStatistics.ToList());
        }

        private Metadata UnauthorizedHeaders() => new Metadata
        {
            { "User-Agent", UserAgent },
        };

        private Metadata AuthorizedHeaders() => new Metadata
        {
            { "Authorization", $"Bearer {_lastLoginReply?.AuthToken}" },
            { "User-Agent", UserAgent },
        };

        private SslClientAuthenticationOptions? GetSslClientAuthenticationOptions()
        {
#if !DEBUG
            var secrets = typeof(RemoteApi).Assembly.GetType("Pal.Client.Secrets");
            if (secrets == null)
                return null;

            var pass = secrets.GetProperty("CertPassword")?.GetValue(null) as string;
            if (pass == null)
                return null;

            var manifestResourceStream = typeof(RemoteApi).Assembly.GetManifestResourceStream("Pal.Client.Certificate.pfx");
            if (manifestResourceStream == null)
                return null;

            var bytes = new byte[manifestResourceStream.Length];
            manifestResourceStream.Read(bytes, 0, bytes.Length);

            var certificate = new X509Certificate2(bytes, pass, X509KeyStorageFlags.DefaultKeySet);
            PluginLog.Debug($"Using client certificate {certificate.GetCertHashString()}");
            return new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection()
                {
                    certificate,
                },
            };
#else
            PluginLog.Debug("Not using client certificate");
            return null;
#endif
        }

        public void Dispose()
        {
            PluginLog.Debug("Disposing gRPC channel");
            _channel?.Dispose();
            _channel = null;
        }
    }
}
