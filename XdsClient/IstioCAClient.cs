using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Envoy.Api.V2.Core;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using k8s;
using Istio.V1.Auth;

namespace XdsClient
{
    class IstioCAClient
    {
        const string IstioNamespace = "istio-system";
        
        public static async Task<X509Certificate2> CreateClientCertificateAsync(Kubernetes kubeClient, string istioSdsEndpoint, X509Certificate2 istioCaCert, string podNamespace, string serviceAccountName)
        {
            var saToken = await RetriveSaTokenAsync(kubeClient, serviceAccountName, podNamespace);
            var csr = GenerateCSR($"spiffe://cluster.local/ns/{podNamespace}/sa/{serviceAccountName}");

            var (grpcConnection, sdsClient) = CreateSdsClient(istioSdsEndpoint, istioCaCert);
            var signedCerts = await RequestNewCertificate(csr.Csr, sdsClient, saToken);
            await grpcConnection.ShutdownAsync();
            return ExportAsCertificate(signedCerts, istioCaCert, csr.PrivateKey);
        }

        private static async Task<RepeatedField<string>> RequestNewCertificate(byte[] csr, IstioCertificateService.IstioCertificateServiceClient sdsClient, string jwtToken)
        {
            var request = new IstioCertificateRequest()
            {
                Csr = BytesToPem(csr, "CERTIFICATE REQUEST"),
                ValidityDuration = (long)TimeSpan.FromHours(1).TotalSeconds
            };
            var headers = new Grpc.Core.Metadata();
            headers.Add("Authorization", "Bearer " + jwtToken);
            headers.Add("ClusterID", "Kubernetes");
            var istioCertificateResponse = await sdsClient.CreateCertificateAsync(request, new CallOptions(headers)).ResponseAsync;
            return istioCertificateResponse.CertChain;
        }


        private static (ChannelBase, IstioCertificateService.IstioCertificateServiceClient) CreateSdsClient(string endpoint, X509Certificate serverCertificate)
        {
            var grpcConnection = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
            {
                HttpHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = CreateCertificateValidator(serverCertificate)
                }
            });
            
            return (grpcConnection, new IstioCertificateService.IstioCertificateServiceClient(grpcConnection));
        }

        private static ClientCsrRequest GenerateCSR(string spiffeURI)
        {
            var subjectName = $"CN={spiffeURI},O=XdsClient,OU=jijiechen,T=Shenzhen,C=China";
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddUri(new Uri(spiffeURI));

            using var privateKey = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(subjectName, privateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment , false));
            certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2" /* Client Authentication */) }, false));
            certificateRequest.CertificateExtensions.Add(sanBuilder.Build());
            
            var keyBytes = privateKey.ExportRSAPrivateKey();
            var csrDerBytes = certificateRequest.CreateSigningRequest(X509SignatureGenerator.CreateForRSA(privateKey, RSASignaturePadding.Pkcs1));
            return new ClientCsrRequest
            {
                Csr = csrDerBytes,
                PrivateKey = keyBytes
            };
        }

        private static async Task<string> RetriveSaTokenAsync(Kubernetes kubeClient, string saName, string namespaceName)
        {
            var istiodServiceAccount = await kubeClient.ReadNamespacedServiceAccountAsync(saName, namespaceName);
            var secretName = istiodServiceAccount.Secrets[0].Name;

            var saSecret = await kubeClient.ReadNamespacedSecretAsync(secretName, namespaceName);
            return Encoding.ASCII.GetString(saSecret.Data["token"]);
        }

        public static async Task<X509Certificate2> GetIstiodCACertAsync(Kubernetes kubeClient)
        {
            var istioCaSecret = await kubeClient.ReadNamespacedSecretAsync("istio-ca-secret", IstioNamespace);
            var caBytes = istioCaSecret.Data["ca-cert.pem"];
            
            return new X509Certificate2(caBytes); 
        }
        
        public static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> CreateCertificateValidator(X509Certificate validCaCert)
        {
            var trustedRoot = validCaCert.GetRawCertData();
            return (HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors policyErrors) =>
            {
                if (policyErrors == SslPolicyErrors.None)
                    return true;

                using var trustedRootCert = new X509Certificate2(trustedRoot);
                return ValidateCertChain(chain, certificate, trustedRootCert);
            };
        }

        private static bool ValidateCertChain(X509Chain chain, X509Certificate2 certificate, X509Certificate2 trustedRootCert)
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(trustedRootCert);
            
            if (!chain.Build(certificate))
            {
                return false;
            }

            if (chain.ChainStatus.Length > 1 || chain.ChainStatus[0].Status != X509ChainStatusFlags.UntrustedRoot)
            {
                return false;
            }
            
            return chain.ChainElements
                .Cast<X509ChainElement>()
                .Any(x => x.Certificate.Thumbprint == trustedRootCert.Thumbprint);
        }

        private static X509Certificate2 ExportAsCertificate(RepeatedField<string> returnedCertChain, X509Certificate2 trustedRootCert, byte[] keyBytes)
        {
            var certs = new List<X509Certificate2>();
            foreach (var certPem in returnedCertChain)
            {
                var bytes = PemToBytes(certPem, "CERTIFICATE");
                certs.Add(new X509Certificate2(bytes));
            }
            
            using var chain = new X509Chain();
            foreach (var childCert in certs)
            {
                chain.ChainPolicy.ExtraStore.Add(childCert);
            }

            var thisCertificate = certs[0];
            var timeOffset = DateTime.Now - thisCertificate.NotBefore;
            if (timeOffset.TotalMilliseconds < 0)
            {
                if (timeOffset.TotalMilliseconds < 10_000)
                {
                    // sleep a short time to wait for the certificate to take effect
                    Thread.Sleep((int)Math.Floor(timeOffset.TotalSeconds) + 1);
                }
                else
                {
                    throw new InvalidOperationException($"It's not possible to request a valid certificate from istio because the system time is much slower than the istio server: {timeOffset.TotalSeconds}s");    
                }
            }
            if (!ValidateCertChain(chain, thisCertificate, trustedRootCert))
            {
                throw new InvalidOperationException("The signed certificate is not signed by the Istio CA");
            }
            using var privateKey = RSA.Create();
            privateKey.ImportRSAPrivateKey(keyBytes, out _);
            using var certWithKeyEphemeral = thisCertificate.CopyWithPrivateKey(privateKey);
            var certWithKey = new X509Certificate2(certWithKeyEphemeral.Export(X509ContentType.Pfx));
            return certWithKey;
        }

        private static string BytesToPem(byte[] bytes, string pemDeclaration)
        {
            var base64 = Convert.ToBase64String(bytes);
            var builder = new StringBuilder();
            builder.AppendLine($"-----BEGIN {pemDeclaration}-----");

            var offset = 0;
            const int lineLength = 64;
            while (offset < base64.Length)
            {
                var lineEnd = Math.Min(offset + lineLength, base64.Length);
                builder.AppendLine(base64.Substring(offset, lineEnd - offset));
                offset = lineEnd;
            }
 
            builder.AppendLine($"-----END {pemDeclaration}-----");
            return builder.ToString();
        }

        private static byte[] PemToBytes(string pemString, string pemDeclaration)
        {
            var beginSign = $"-----BEGIN {pemDeclaration}-----";
            var endSign = $"-----END {pemDeclaration}-----";
            var base64 = pemString.Replace(beginSign, string.Empty)
                .Replace(endSign, string.Empty)
                .Replace("\n", string.Empty);
            return Convert.FromBase64String(base64);
        }

        class ClientCsrRequest
        {
            public byte[] Csr { get; set; }
            public byte[] PrivateKey { get; set; }
        }
    }
}


