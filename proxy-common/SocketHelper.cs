using System.Net;
using System.Net.Sockets;

namespace proxy_common;

public interface ISocketHelper
{
    Task<IWrappedSocket> ConnectToServer(string ipOrHost, int port);
    Task ListenForConnections(int port, Func<IWrappedSocket, Task> onNewConnection);
    Task<ReadMessageResult> ReadSocketUntilError(IWrappedSocket socket, int maxResultLen, Func<Memory<byte>, Task<ReadMessageResult>> onNewData);
}

public record ReadMessageResult(bool ConnectionError);

public class SocketHelper : ISocketHelper
{
    private readonly ILogger _logger;

    public SocketHelper(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<IWrappedSocket> ConnectToServer(string ipOrHost, int port)
    {
        try
        {
            var ipAddress = await ParseIPAddress(ipOrHost);
            var ipEndPoint = new IPEndPoint(ipAddress, port);
            var socket = new WrappedSocket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp, _logger);
            await socket.ConnectAsync(ipOrHost, port);
            return socket;
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in ConnectToServer. {e}");
            throw;
        }
    }

    public async Task ListenForConnections(int port, Func<IWrappedSocket, Task> onNewConnection)
    {
        try
        {
            using var socket = new WrappedSocket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, _logger);
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            socket.Listen(100);

            while (true)
            {
                var socketClient = await socket.AcceptAsync();

                new Task(async () =>
                {
                    if (await CanReadFromSocket(socketClient))
                    {
                        await onNewConnection(socketClient);
                    }
                    else
                    {
                        socketClient.Close();
                    }
                }, TaskCreationOptions.LongRunning)
                .Start();
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

    private static async Task<bool> CanReadFromSocket(IWrappedSocket socket)
    {
        var buffer = new byte[1];
        var readSocketTask = socket.ReceiveAsync(buffer, SocketFlags.Peek);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(4));

        if (await Task.WhenAny(readSocketTask, timeoutTask) == readSocketTask)
        {
            return true;
        }
        return false;
    }

    public async Task<ReadMessageResult> ReadSocketUntilError(IWrappedSocket socket, int maxResultLen, Func<Memory<byte>, Task<ReadMessageResult>> onNewData)
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
                    _logger.Info($"Received 0 bytes. This indicates a connection error. Socket handle: {socket.Handle}");
                    return new ReadMessageResult(ConnectionError: true);
                }
                // _logger.Info($"End ReadSocket. socket handle: {socket.Handle}");
            }
        }
        catch (ObjectDisposedException e)
        {
            _logger.Error($"ObjectDisposedException in ReadSocket. Socket closed. Socket handle: {socket.Handle}. {e}");
            return new ReadMessageResult(ConnectionError: true);
        }
        catch (SocketException e)
        {
            _logger.Error($"SocketException in ReadSocket. Socket handle: {socket.Handle}. SocketException: {e.Message}");
            return new ReadMessageResult(ConnectionError: true);
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
