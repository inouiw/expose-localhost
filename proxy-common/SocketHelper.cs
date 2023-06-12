using System.Net;
using System.Net.Sockets;

namespace proxy_common;

public interface ISocketHelper
{
    Task<Socket> ConnectToServer(string ipOrHost, int port);
    Task ListenForConnections(int port, Func<Socket, Task> onNewConnection);
    Task<ReadMessageResult> ReadSocket(Socket socket, int maxResultLen, Func<Memory<byte>, Task<ReadMessageResult>> onNewData);
}

public record ReadMessageResult(bool ConnectionError);

public class SocketHelper : ISocketHelper
{
    private readonly ILogger _logger;

    public SocketHelper(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<Socket> ConnectToServer(string ipOrHost, int port)
    {
        try
        {
            var ipAddress = await ParseIPAddress(ipOrHost);
            var ipEndPoint = new IPEndPoint(ipAddress, port);
            var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ipOrHost, port);
            return socket;
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in ConnectToServer. {e}");
            throw;
        }
    }

    public async Task ListenForConnections(int port, Func<Socket, Task> onNewConnection)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            socket.Listen(100);

            while (true)
            {
                var socketClient = await socket.AcceptAsync();
                await onNewConnection(socketClient);
            }
        }
        catch (SocketException e)
        {
            _logger.Error($"SocketException in ListenForConnections on port {port}. {e}");
            throw;
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in ListenForConnections. {e}");
            throw;
        }
    }

    public async Task<ReadMessageResult> ReadSocket(Socket socket, int maxResultLen, Func<Memory<byte>, Task<ReadMessageResult>> onNewData)
    {
        try
        {
            while (true)
            {
                // _logger.Info($"Begin ReadSocket. socket handle: {socket.Handle}");
                var buffer = new byte[maxResultLen];
                var memory = new Memory<byte>(buffer);
                int received = await socket.ReceiveAsync(memory);
                if (received > 0)
                {
                    var result = await onNewData(memory[..received]);
                    if (result.ConnectionError)
                    {
                        return result;
                    }
                }
                else
                {
                    _logger.Info($"received 0 bytes. Socket handle: {socket.Handle}");
                    return new ReadMessageResult(ConnectionError: true);
                }
                // _logger.Info($"End ReadSocket. socket handle: {socket.Handle}");
            }
        }
        catch (ObjectDisposedException)
        {
            _logger.Error($"ObjectDisposedException in ReadSocket. Socket closed. Socket handle: {socket.Handle}");
            throw;
        }
        catch (SocketException e)
        {
            _logger.Error($"SocketException in ReadSocket. Socket handle: {socket.Handle}. SocketException: {e}");
            throw;
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in ReadSocket. Socket handle: {socket.Handle}. {e}");
            throw;
        }
    }

    private async static Task<IPAddress> ParseIPAddress(string ipOrHost)
    {
        if (IPAddress.TryParse(ipOrHost.Trim(), out var iPAddress))
        {
            return iPAddress;
        }
        var ipHostInfo = await Dns.GetHostEntryAsync(ipOrHost);
        return ipHostInfo.AddressList[0];
    }
}
