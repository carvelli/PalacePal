using Dalamud.Logging;
using Grpc.Core;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Pal.Client.Net
{
    internal partial class RemoteApi
    {
        private Metadata UnauthorizedHeaders() => new Metadata
        {
            { "User-Agent", UserAgent },
        };

        private Metadata AuthorizedHeaders() => new Metadata
        {
            { "Authorization", $"Bearer {_loginInfo?.AuthToken}" },
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

        public bool HasRoleOnCurrentServer(string role)
        {
            if (Service.Configuration.Mode != Configuration.EMode.Online)
                return false;

            var account = Account;
            return account == null || account.CachedRoles.Contains(role);
        }
    }
}
