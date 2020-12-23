using System;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using k8s;

namespace XdsClient
{
    class IstioCAClient
    {
        public static async Task<X509Certificate> CreateClientCertificateAsync(string uriSAN)
        {
            var jwtToken = await RetriveIstiodSATokenAsync();

            // https://github.com/istio/istio/security/pkg/nodeagent/caclient/providers/citadel/client.go
            // https://github.com/istio/api/blob/master/security/v1alpha1/ca.pb.go#L212
            // "/istio.v1.auth.IstioCertificateService/CreateCertificate
            
            // kubectl get serviceaccount/istiod-service-account -n istio-system -o 'jsonpath={.secrets[0].name}'
            // kubectl get secret/istiod-service-account-token-v55nh -n istio-system -o 'jsonpath={.data.token}'
            
            return new X509Certificate2(@"C:\Users\jijie\Documents\Projects\XdsClient\XdsClient\Certs\client.pfx", string.Empty);
        }

        private static async Task<string> RetriveIstiodSATokenAsync()
        {
            const string istioNamespace = "istio-system";

            var kubeClient = new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());
            var istiodServiceAccount = await kubeClient.ReadNamespacedServiceAccountAsync("istiod-service-account", istioNamespace);
            var secretName = istiodServiceAccount.Secrets[0].Name;

            var saSecret = await kubeClient.ReadNamespacedSecretAsync(secretName, istioNamespace);
            return Encoding.ASCII.GetString(saSecret.Data["token"]);
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