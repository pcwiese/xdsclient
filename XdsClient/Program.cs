using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;

namespace XdsClient
{
    public class Runner
    {
        static async Task Main(string[] args)
        {
            var role = ProxyRole.Router.ToString().ToLowerInvariant();
            var k8sNamespace = "fake-ns";
            var serviceAccountName = "bookinfo-details";
            var nodeId = $"{role}~192.168.1.1~fake-node.{k8sNamespace}~{k8sNamespace}.svc.cluster.local";
            var istiodURL = "https://192.168.1.152:15012";

            var clientCertificate = await IstioCAClient.CreateClientCertificateAsync($"spiffe://cluster.local/ns/{k8sNamespace}/sa/{serviceAccountName}");
            var serverCertificate = await IstioCAClient.GetIstiodCACertAsync();

            var adsClient = CreateXdsClient(clientCertificate, serverCertificate, istiodURL);
            await ListClustersAsync(nodeId, adsClient);
        }

        private static async Task ListClustersAsync(string nodeId, AggregatedDiscoveryService.AggregatedDiscoveryServiceClient adsClient)
        {
            var dr = new DiscoveryRequest() {Node = new Node() {Metadata = new Struct()}};
            dr.Node.Id = nodeId;
            dr.TypeUrl = EnvoyTypeConstants.ClusterType;

            var call = adsClient.StreamAggregatedResources();
            await call.RequestStream.WriteAsync(dr);
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                foreach (var res in call.ResponseStream.Current.Resources)
                {
                    var cluster = res.Unpack<Cluster>();
                    Console.WriteLine(cluster);
                }
            }
        }

        private static AggregatedDiscoveryService.AggregatedDiscoveryServiceClient CreateXdsClient(X509Certificate clientCertificate,  X509Certificate serverCertificate, string istiodURL)
        {
            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(clientCertificate);
            handler.ServerCertificateCustomValidationCallback = IstioCAClient.CreateCertificateValidator(serverCertificate);

            return new AggregatedDiscoveryService.AggregatedDiscoveryServiceClient(GrpcChannel.ForAddress(istiodURL, new GrpcChannelOptions
            {
                HttpHandler = handler
            }));
        }
    }
}