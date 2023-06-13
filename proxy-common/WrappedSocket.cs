using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace proxy_common;

public interface IWrappedSocket
{
    ValueTask<int> SendAsync(Memory<byte> buffer);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer);
    Task<int> ReceiveAsync(byte[] buffer);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer);
    Task ConnectAsync(string host, int port);
    void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, bool optionValue);
    void Bind(EndPoint localEP);
    void Listen(int backlog);
    Task<IWrappedSocket> AcceptAsync();
    Task<int> ReceiveAsync(ArraySegment<byte> buffer, SocketFlags socketFlags);
    nint Handle { get; }
    void Close([CallerMemberName] string? caller = null);
    IPAddress? RemoteAddress { get; }
    int? RemotePort { get; }
}

public class WrappedSocket : IWrappedSocket, IDisposable
{
    private readonly Socket _socket;
    private readonly ILogger _logger;

    public WrappedSocket(Socket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
    }

    public WrappedSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, ILogger logger)
    {
        _socket = new Socket(addressFamily, socketType, protocolType);
        _logger = logger;
    }

    public ValueTask<int> SendAsync(Memory<byte> buffer)
    {
        return _socket.SendAsync(buffer);
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer)
    {
        return _socket.SendAsync(buffer);
    }

    public Task<int> ReceiveAsync(byte[] buffer)
    {
        return _socket.ReceiveAsync(buffer);
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer)
    {
        return _socket.ReceiveAsync(buffer);
    }

    public Task ConnectAsync(string host, int port)
    {
        return _socket.ConnectAsync(host, port);
    }

    public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, bool optionValue)
    {
        _socket.SetSocketOption(optionLevel, optionName, optionValue);
    }

    public void Bind(EndPoint localEP)
    {
        _socket.Bind(localEP);
    }

    public void Listen(int backlog)
    {
        _socket.Listen(backlog);
    }

    public async Task<IWrappedSocket> AcceptAsync()
    {
        return new WrappedSocket(await _socket.AcceptAsync(), _logger);
    }

    public Task<int> ReceiveAsync(ArraySegment<byte> buffer, SocketFlags socketFlags)
    {
        return _socket.ReceiveAsync(buffer, socketFlags);
    }

    public nint Handle => _socket.Handle;

    public void Close([CallerMemberName] string? caller = null)
    {
        _logger.Info($"Closing socket. Handle: {_socket.Handle}, Caller: {caller}");
        _socket.Close();
    }

    public void Dispose()
    {
        _logger.Info($"Disposing socket. Handle: {_socket.Handle}");
        _socket.Dispose();
        GC.SuppressFinalize(this);
    }

    public IPAddress? RemoteAddress => (_socket.RemoteEndPoint as IPEndPoint)?.Address;
    public int? RemotePort => (_socket.RemoteEndPoint as IPEndPoint)?.Port;
}