using System;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace XdsClient
{
    class IstioCAClient
    {
        public static async Task<X509Certificate> CreateClientCertificateAsync(string uriSAN)
        {
            return new X509Certificate2(@"C:\Users\jijie\Documents\Projects\XdsClient\XdsClient\client.pfx", string.Empty);
        }

        public static async Task<X509Certificate> GetIstiodCACertAsync()
        {
            return new X509Certificate(@"C:\Users\jijie\Documents\Projects\XdsClient\XdsClient\Certs\ca-cert.pem");
        }
        
        public static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> CreateCertificateValidator(X509Certificate validCaCert)
        {
            // todo: complete the certificate validation process
            return (HttpRequestMessage request, X509Certificate2 certificate, X509Chain certificateChain, SslPolicyErrors policyErrors) =>
            {
                return true;
                var trustedRoot = validCaCert.GetRawCertData();
                foreach (var element in certificateChain.ChainElements)
                {
                    foreach (var status in element.ChainElementStatus)
                    {
                        if (trustedRoot.SequenceEqual(element.Certificate.RawData))
                            return true;
                    }
                }

                return certificateChain.ChainStatus.All(s => s.Status != X509ChainStatusFlags.UntrustedRoot);
            };
        }
    }
}