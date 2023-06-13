using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace proxy_common;

public interface IWrappedSocket : IDisposable
{
    ValueTask<int> SendAsync(Memory<byte> buffer);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer);
    Task<int> ReceiveAsync(byte[] buffer);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer);
    Task<int> ReceiveAsync(ArraySegment<byte> buffer, SocketFlags socketFlags);
    Task ConnectAsync(string host, int port);
    void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, bool optionValue);
    void Bind(EndPoint localEP);
    void Listen(int backlog);
    Task<IWrappedSocket> AcceptAsync(string socketNamePrefix);
    nint Handle { get; }
    void Close([CallerMemberName] string? caller = null);
    IPAddress? RemoteAddress { get; }
    int? RemotePort { get; }
    string Name { get; }
}

public class WrappedSocket : IWrappedSocket, IDisposable
{
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly object _sendLock = new();
    private readonly object _receiveLock = new();

    public WrappedSocket(Socket socket, ILogger logger, string socketNamePrefix)
    {
        _socket = socket;
        _logger = logger;
        _name = $"{socketNamePrefix}_Handle-{_socket.Handle}";
    }

    public WrappedSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, ILogger logger, string socketNamePrefix)
    {
        _socket = new Socket(addressFamily, socketType, protocolType);
        _logger = logger;
        _name = $"{socketNamePrefix}_Handle-{_socket.Handle}";
    }

    public ValueTask<int> SendAsync(Memory<byte> buffer)
    {
        lock (_sendLock)
        {
            return _socket.SendAsync(buffer);
        }
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer)
    {
        lock (_sendLock)
        {
            return _socket.SendAsync(buffer);
        }
    }

    public Task<int> ReceiveAsync(byte[] buffer)
    {
        lock (_receiveLock)
        {
            return _socket.ReceiveAsync(buffer);
        }
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer)
    {
        lock (_receiveLock)
        {
            return _socket.ReceiveAsync(buffer);
        }
    }

    public Task<int> ReceiveAsync(ArraySegment<byte> buffer, SocketFlags socketFlags)
    {
        lock (_receiveLock)
        {
            return _socket.ReceiveAsync(buffer, socketFlags);
        }
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

    public async Task<IWrappedSocket> AcceptAsync(string socketNamePrefix)
    {
        var socket = await _socket.AcceptAsync();
        return new WrappedSocket(socket, _logger, socketNamePrefix);
    }

    public nint Handle => _socket.Handle;

    public void Close([CallerMemberName] string? caller = null)
    {
        _logger.Info($"Closing socket. Name: {Name}, Caller: {caller}");
        _socket.Close();
    }

    public void Dispose()
    {
        _logger.Info($"Disposing socket. Name: {Name}");
        _socket.Dispose();
        GC.SuppressFinalize(this);
    }

    public IPAddress? RemoteAddress => (_socket.RemoteEndPoint as IPEndPoint)?.Address;
    public int? RemotePort => (_socket.RemoteEndPoint as IPEndPoint)?.Port;
    public string Name => _name;
}