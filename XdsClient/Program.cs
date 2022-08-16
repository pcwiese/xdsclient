using System;
using System.Collections.Generic;
using System.Linq;
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
    // given pod, read its configuration, get istiod url
    // unpack typed config automatically
    // port-forward istio xds-grpc
    // ui
    // request cert from real ca
    
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
                var istiodURL = optionIstioURL.HasValue() ? optionIstioURL.Value() : (Environment.GetEnvironmentVariable("ISTIOD_URL") ?? "http://localhost:30011");
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

            var clusterId = istioProxy.Env.Single(e => e.Name == "ISTIO_META_CLUSTER_ID").Value;
            var isSidecar = istioProxy.Args.Any(x => x == "sidecar");
            var nodeRole = isSidecar ? "sidecar" : "router";
            var nodeId = $"{nodeRole}~{podip}~{podName}.{kubeConfig.Namespace}~{kubeConfig.Namespace}.svc.cluster.local";

            using var channel = GrpcChannel.ForAddress(istiodUrl);
            var client = new AggregatedDiscoveryService.AggregatedDiscoveryServiceClient(channel);

            var request = CreateDiscoveryRequest(clusterId, nodeId, EnvoyTypeConstants.EndpointType);
            ////var request = CreateDiscoveryRequest(clusterId, nodeId, EnvoyTypeConstants.ListenerType);
            var resourceNames = new Google.Protobuf.Collections.RepeatedField<string>();
            request.ResourceNames.Add("outbound|40041||media-transformer.tts-frontend.svc.cluster.local");
            ////request.ResourceNames.Add("media-transformer.tts-frontend.svc.cluster.local:40041");
            ////ldsRequest.ResourceNamesSubscribe.Add("media-transformer-service.tts-frontend.svc.cluster.local:40041");

            using var call = client.StreamAggregatedResources();
            await call.RequestStream.WriteAsync(request);

            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                var response = call.ResponseStream.Current;

                // ACK it
                var ack = CreateDiscoveryRequest(clusterId, nodeId);
                ack.ResponseNonce = response.Nonce;
                await call.RequestStream.WriteAsync(ack);

                var clusterLoadAssignment = response.Resources.Select(r => r.Unpack<ClusterLoadAssignment>()).FirstOrDefault();
                var endpoints = clusterLoadAssignment?.Endpoints.SelectMany(x => x.LbEndpoints.Select(y => y.Endpoint));
                ////var addresses = new List<BalancerAddress>(endpoints != null ? endpoints.Count() : 0);
                if (endpoints != null)
                {
                    foreach (var endpoint in endpoints)
                    {
                        ////addresses.Add(new BalancerAddress(new DnsEndPoint(new IPAddress(endpoint.Address).ToString(), endpoint.Address.SocketAddress)));
                    }
                }
            }

            ////var xdsResources = await ListResourcesAsync(clusterId, nodeId, client);
            ////await channel.ShutdownAsync();

            ////Console.WriteLine("======= Clusters =======");
            ////foreach (var cluster in xdsResources.Clusters)
            ////{
            ////    Console.WriteLine(cluster);
            ////}

            ////Console.WriteLine("======= Endpoints =======");
            ////foreach (var endpoint in xdsResources.Endpoints)
            ////{
            ////    Console.WriteLine(endpoint);
            ////}

            ////Console.WriteLine("======= Listeners =======");
            ////foreach (var listener in xdsResources.Listeners)
            ////{
            ////    Console.WriteLine(listener);
            ////}

            ////Console.WriteLine("======= Routes =======");
            ////foreach (var route in xdsResources.Routes)
            ////{
            ////    Console.WriteLine(route);
            ////}
        }

        private static async Task<XdsResources> ListResourcesAsync(string clusterId, string nodeId, AggregatedDiscoveryService.AggregatedDiscoveryServiceClient adsClient)
        {
            var resources = new XdsResources();
            ////resources.Clusters = await FetchResources<Cluster>(adsClient, clusterId, nodeId, EnvoyTypeConstants.ClusterType, new List<string> { "*" });

            ////var endpointNames = resources.Clusters
            ////    .Select(c => c.EdsClusterConfig?.ServiceName)
            ////    .Where(e => e != null)
            ////    .Distinct()
            ////    .ToList();
            ////resources.Endpoints = await FetchResources<ClusterLoadAssignment>(adsClient, clusterId, nodeId, EnvoyTypeConstants.EndpointType, endpointNames);

            resources.Listeners = await FetchResources<Listener>(adsClient, clusterId, nodeId, EnvoyTypeConstants.ListenerType, new List<string> { "media-transformer.tts-frontend:40041" });
            //resources.Routes = await FetchResources<RouteConfiguration>(adsClient, clusterId, nodeId, EnvoyTypeConstants.RouteType, GetRouteNames(resources.Listeners));
            
            return resources;
        }

        private static async Task<List<T>> FetchResources<T>(AggregatedDiscoveryService.AggregatedDiscoveryServiceClient adsClient, 
            string clusterId, string nodeId, string resourceTypeString, List<string> resourceNames) where T : IMessage, new()
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
                            ["CLUSTER_ID"] = Value.ForString(clusterId)
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
                        .Select(chain => chain.Filters?.FirstOrDefault(f => f.Name == "envoy.http_connection_manager" || f.Name == "envoy.filters.network.http_connection_manager"))
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

        private static DiscoveryRequest CreateDiscoveryRequest(string clusterId, string nodeId, string typeUrl = default)
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
                            ["CLUSTER_ID"] = Value.ForString(clusterId)
                        }
                    }
                },
            };

            if (typeUrl != default)
            {
                request.TypeUrl = typeUrl;
            }

            return request;
        }

        class XdsResources
        {
            public List<Cluster> Clusters { get; set; } = new List<Cluster>();
            public List<ClusterLoadAssignment> Endpoints { get; set; } = new List<ClusterLoadAssignment>();
            public List<Listener> Listeners { get; set; } = new List<Listener>();
            public List<RouteConfiguration> Routes { get; set; } = new List<RouteConfiguration>();
            
        }
    }
}