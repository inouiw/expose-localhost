using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using proxy_common;

int connectionIdCounter = 0;
var connectionIdToNonProxyClient = new ConcurrentDictionary<int, Socket>();
var connectedProxyClient = (Socket?)null;

int portListenForProxyClients = 4000;
int portListenForNonProxyClients = 443;

ILogger _logger = new Logger();
ISocketHelper _socketHelper = new SocketHelper(_logger);
ITcpProxy _tcpProxy = new TcpProxy(_logger, _socketHelper);

var listener1 = _socketHelper.ListenForConnections(portListenForProxyClients, (socketClient) =>
{
    var ipEndPoint = socketClient.RemoteEndPoint as IPEndPoint;
    _logger.Info($"Proxy client connected. IP: {ipEndPoint?.Address}, port: {ipEndPoint?.Port}, Socket handle: {socketClient.Handle}");
    connectedProxyClient = socketClient;
    return _tcpProxy.ReadMessage(socketClient, async (buffer, connectionId) =>
    {
        var nonProxyClient = connectionIdToNonProxyClient[connectionId];
        await nonProxyClient.SendAsync(buffer);
    });
});

var listener2 = _socketHelper.ListenForConnections(portListenForNonProxyClients, (socketClient) =>
{
    int connectionId = ++connectionIdCounter;
    var ipEndPoint = socketClient.RemoteEndPoint as IPEndPoint;
    _logger.Info($"Browser client connected. IP: {ipEndPoint?.Address}, port: {ipEndPoint?.Port}, Socket handle: {socketClient.Handle}, connectionId: {connectionId}");
    connectionIdToNonProxyClient.TryAdd(connectionId, socketClient);
    return Task.Run(() => _socketHelper.ReadSocket(socketClient, TcpProxy.WrappedDataMessageMaxLen, async (buffer) =>
    {
        if (connectedProxyClient != null)
        {
            _logger.Info($"Writing to proxy client. {buffer.Length} bytes including header data, {buffer.Length - TcpProxy.DataMessageHeaderLen} bytes excluding header data.");
            await _tcpProxy.WriteMessage(connectedProxyClient, connectionId, buffer);
        }
        return new ReadMessageResult(ConnectionError: false);
    }));
});

_logger.Info("ProxyServer waiting for connections");
await Task.WhenAny(listener1, listener2);
_logger.Info("ProxyServer exiting");
