using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Envoy.Config.Listener.V3;
using Envoy.Config.Route.V3;
using Envoy.Extensions.Filters.Network.HttpConnectionManager.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using k8s;
using Extension = Envoy.Config.Core.V3.Extension;

namespace XdsClient
{
    public class Runner
    {
        static async Task Main(string[] args)
        {
            var istiodURL = "https://192.168.1.152:15012";
            var k8sNamespace = "bookinfo";
            var role = ProxyRole.Router.ToString().ToLowerInvariant();
            var nodeId = $"{role}~192.168.1.1~fake-node.{k8sNamespace}~{k8sNamespace}.svc.cluster.local";

            using var kubeClient = new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());
            var validIstioCaCertificate = await IstioCAClient.GetIstiodCACertAsync(kubeClient);
            var clientCertificate = await IstioCAClient.CreateClientCertificateAsync(kubeClient, istiodURL, validIstioCaCertificate); 
            var (grpcConnection, adsClient) = CreateXdsClient(clientCertificate, validIstioCaCertificate, istiodURL);

            var xdsResources = await ListResourcesAsync(nodeId, adsClient);
            await grpcConnection.ShutdownAsync();
            
            Console.WriteLine("======= Clusters =======");
            foreach (var cluster in xdsResources.Clusters)
            {
                Console.WriteLine(cluster);
            }
            
            Console.WriteLine("======= Endpoints =======");
            foreach (var endpoint in xdsResources.Endpoints)
            {
                Console.WriteLine(endpoint);
            }
            
            Console.WriteLine("======= Listeners =======");
            foreach (var listener in xdsResources.Listeners)
            {
                Console.WriteLine(listener);
            }
            
            Console.WriteLine("======= Routes =======");
            foreach (var route in xdsResources.Routes)
            {
                Console.WriteLine(route);
            }
        }
        
        private static async Task<XdsResources> ListResourcesAsync(string nodeId, AggregatedDiscoveryService.AggregatedDiscoveryServiceClient adsClient)
        {
            var resources = new XdsResources();
            var streamingCall = adsClient.StreamAggregatedResources();

            resources.Clusters = await FetchResources<Cluster>(nodeId, EnvoyTypeConstants.ClusterType, null, streamingCall);

            var endpointNames = resources.Clusters
                .Select(c => c.EdsClusterConfig?.ServiceName)
                .Where(e => e != null)
                .Distinct()
                .ToList();
            resources.Endpoints = await FetchResources<ClusterLoadAssignment>(nodeId, EnvoyTypeConstants.EndpointType, endpointNames, streamingCall);
            
            resources.Listeners = await FetchResources<Listener>(nodeId, EnvoyTypeConstants.ListenerType, null, streamingCall);
            var routeNames = GetRouteNames(resources.Listeners);
            resources.Routes = await FetchResources<RouteConfiguration>(nodeId, EnvoyTypeConstants.RouteType, routeNames, streamingCall);
            
            return resources;
        }

        private static async Task<List<T>> FetchResources<T>(string nodeId, string resourceTypeString, List<string> resourceNames, AsyncDuplexStreamingCall<DiscoveryRequest, DiscoveryResponse> call) where T : IMessage, new()
        {
            var request = new DiscoveryRequest
            {
                Node = new Node()
                {
                    Id = nodeId,
                    Metadata = new Struct()
                }, 
                TypeUrl = resourceTypeString
            };
            if (resourceNames != null)
            {
                request.ResourceNames.AddRange(resourceNames);
            }
            await call.RequestStream.WriteAsync(request);
            
            await call.ResponseStream.MoveNext(CancellationToken.None);
            var resources = call.ResponseStream.Current.Resources.Select(res => res.Unpack<T>()).ToList();
            return resources;
        }

        private static (ChannelBase, AggregatedDiscoveryService.AggregatedDiscoveryServiceClient) CreateXdsClient(X509Certificate clientCertificate,  X509Certificate serverCertificate, string istiodURL)
        {
            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(clientCertificate);
            handler.ServerCertificateCustomValidationCallback = IstioCAClient.CreateCertificateValidator(serverCertificate);

            var grpcConnection = GrpcChannel.ForAddress(istiodURL, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
            return (grpcConnection, new AggregatedDiscoveryService.AggregatedDiscoveryServiceClient(grpcConnection));
        }

        private static List<string> GetRouteNames(List<Listener> listeners)
        {
            return listeners.Select(l =>
                {
                    var httpFilterChain = l.FilterChains?.FirstOrDefault(chain =>
                        chain.FilterChainMatch?.ApplicationProtocols != null &&
                        chain.FilterChainMatch.ApplicationProtocols.Contains("http/1.1"));
                    var hcm = httpFilterChain?.Filters?.FirstOrDefault(f => f.Name == "envoy.http_connection_manager");
                    return hcm?.TypedConfig;
                }).Where(hcmConfig => hcmConfig != null)
                .Select(hcmConfig =>
                {
                    var hcm = hcmConfig.Unpack<HttpConnectionManager>();
                    return hcm.Rds?.RouteConfigName;
                })
                .Where(r => r != null)
                .Distinct()
                .ToList();
        }

        class XdsResources
        {
            public List<Cluster> Clusters { get; set; }
            public List<ClusterLoadAssignment> Endpoints { get; set; }
            public List<Listener> Listeners { get; set; }
            public List<RouteConfiguration> Routes { get; set; }
            
        }
    }
}