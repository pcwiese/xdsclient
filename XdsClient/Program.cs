using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
using McMaster.Extensions.CommandLineUtils;

namespace XdsClient
{
    // read istio url, cluster-id from pod parameters
    // given pod, read its configuration
    // unpack typed config automatically
    // ui
    // port-forward istio xds-grpc
    
    public class Program
    {
        static async Task Main(string[] args)
        {
            
            var app = new CommandLineApplication();

            app.HelpOption();
            var optionNamespace = app.Option("-n|--namespace <NAMESPACE>", "The Kubernetes namespace name in which the node is placed at.", CommandOptionType.SingleValue);
            var optionIstioURL = app.Option("--istiod <ISTIO_PILOT_URL>", "Specify the URL of the istio pilot server (istiod).", CommandOptionType.SingleValue);
            var targetPodName = app.Argument("pod-name", "target pod to play", false);

            app.OnExecuteAsync(async (cancellationToken) =>
            {
                var k8sNamespace = optionNamespace.HasValue() ? optionNamespace.Value() : null;
                var istiodURL = optionIstioURL.HasValue() ? optionIstioURL.Value() : (Environment.GetEnvironmentVariable("ISTIOD_URL") ?? "https://localhost:15012");
                var k8sConfig = KubernetesClientConfiguration.BuildDefaultConfig();
                if (!string.IsNullOrEmpty(k8sNamespace))
                {
                    k8sConfig.Namespace = k8sNamespace;
                }
                if (string.IsNullOrEmpty(k8sConfig.Namespace))
                {
                    k8sConfig.Namespace = "default";
                }
                
                var kubeClient = new Kubernetes(k8sConfig);
                await PrintResources(kubeClient, k8sConfig, targetPodName.Value, istiodURL);
            });

            await app.ExecuteAsync(args);
        }

        private static async Task PrintResources(Kubernetes kubeClient, KubernetesClientConfiguration kubeConfig, string podName, string istiodUrl)
        {
            var podResp = await kubeClient.ReadNamespacedPodWithHttpMessagesAsync(podName, kubeConfig.Namespace);
            var pod = podResp.Body;
            var podip = pod.Status.PodIP;
            var istioProxy = pod.Spec.Containers.FirstOrDefault(c => c.Name == "istio-proxy");
            if (istioProxy == null)
            {
                await Console.Error.WriteLineAsync($"Pod {podName} does not have a sidecar injected, and it is not a gateway.");
                return;
            }

            var isSidecar = istioProxy.Args.Any(x => x == "sidecar");
            var nodeRole = isSidecar ? "sidecar" : "router";
            var nodeId = $"{nodeRole}~{podip}~{podName}.{kubeConfig.Namespace}~{kubeConfig.Namespace}.svc.cluster.local";

            var (grpcConnection, adsClient) = await CreateXdsClient(kubeClient, istiodUrl, kubeConfig.Namespace, pod.Spec.ServiceAccountName);
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
            resources.Clusters = await FetchResources<Cluster>(adsClient, nodeId, EnvoyTypeConstants.ClusterType, null);

            var endpointNames = resources.Clusters
                .Select(c => c.EdsClusterConfig?.ServiceName)
                .Where(e => e != null)
                .Distinct()
                .ToList();
            resources.Endpoints = await FetchResources<ClusterLoadAssignment>(adsClient, nodeId, EnvoyTypeConstants.EndpointType, endpointNames);
            
            resources.Listeners = await FetchResources<Listener>(adsClient, nodeId, EnvoyTypeConstants.ListenerType, null);
            resources.Routes = await FetchResources<RouteConfiguration>(adsClient, nodeId, EnvoyTypeConstants.RouteType, GetRouteNames(resources.Listeners));
            
            return resources;
        }

        private static async Task<List<T>> FetchResources<T>(AggregatedDiscoveryService.AggregatedDiscoveryServiceClient adsClient, string nodeId, string resourceTypeString, List<string> resourceNames) where T : IMessage, new()
        {
            var request = new DiscoveryRequest
            {
                Node = new Node()
                {
                    Id = nodeId,
                    Metadata = new Struct
                    {
                        Fields =
                        {
                            ["CLUSTER_ID"] = Value.ForString("Kubernetes")
                        }
                    }
                }, 
                TypeUrl = resourceTypeString
            };
            if (resourceNames != null)
            {
                request.ResourceNames.AddRange(resourceNames);
            }
            using var streamingCall = adsClient.StreamAggregatedResources();
            await streamingCall.RequestStream.WriteAsync(request);

            await streamingCall.ResponseStream.MoveNext(CancellationToken.None);
            var resources = streamingCall.ResponseStream.Current.Resources.Select(res => res.Unpack<T>()).ToList();
            return resources;
        }

        private static List<string> GetRouteNames(List<Listener> listeners)
        {
            return listeners.Select(l =>
                {
                    var filter = l.FilterChains?
                        .Select(chain => chain.Filters?.FirstOrDefault(f =>  f.Name == "envoy.http_connection_manager" || f.Name == "envoy.filters.network.http_connection_manager"))
                        .FirstOrDefault();
                    
                    return filter?.TypedConfig;
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

        private static async Task<(ChannelBase, AggregatedDiscoveryService.AggregatedDiscoveryServiceClient)> CreateXdsClient(Kubernetes kubeClient, string istiodURL, string podNamespace, string serviceAccountName)
        {
            
            var validIstioCaCertificate = await IstioCAClient.GetIstiodCACertAsync(kubeClient);
            var clientCertificate = await IstioCAClient.CreateClientCertificateAsync(kubeClient, istiodURL, validIstioCaCertificate, podNamespace, serviceAccountName); 
            
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(clientCertificate);
            handler.ServerCertificateCustomValidationCallback = IstioCAClient.CreateCertificateValidator(validIstioCaCertificate);
            // handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            var grpcConnection = GrpcChannel.ForAddress(istiodURL, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
            return (grpcConnection, new AggregatedDiscoveryService.AggregatedDiscoveryServiceClient(grpcConnection));
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