
Istio xDS .NET Client 客户端
==========

This repo shows how to use .NET code to connect to an Istiod discovery (Pilot) server, and print out its many kinds of resources.

此项目展示了如何使用 .NET 代码连接 Istiod 服务，并输出其中的各种资源（Cluster, Endpoint, Listener, Route）



后续工作：

* 将 Route 合并到 Listener 中展示；将 Endpoint 合并到 Cluster 中展示
* 持续侦听并接收 push，比较新版与旧版本的差异（diff）
* 在代码里自动 port-forward，不需要手工提前 port-forward
* 在持续连接时，处理证书过期的情况（用快过期的证书调用 sds 接口）
* 拉取 EnvoyFilter 资源

