namespace XdsClient
{
    public class EnvoyTypeConstants
    {
        const string ApiTypePrefix       = "type.googleapis.com/";
        
        public const string EndpointType        = ApiTypePrefix + "envoy.config.endpoint.v3.ClusterLoadAssignment";
        public const string ClusterType         = ApiTypePrefix + "envoy.config.cluster.v3.Cluster";
        public const string RouteType           = ApiTypePrefix + "envoy.config.route.v3.RouteConfiguration";
        public const string ListenerType        = ApiTypePrefix + "envoy.config.listener.v3.Listener";
        public const string SecretType          = ApiTypePrefix + "envoy.extensions.transport_sockets.tls.v3.Secret";
        public const string ExtensionConfigType = ApiTypePrefix + "envoy.config.core.v3.TypedExtensionConfig";
        public const string RuntimeType         = ApiTypePrefix + "envoy.service.runtime.v3.Runtime";
    }
}