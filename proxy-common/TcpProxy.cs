using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace proxy_common;

// Protocol between proxy-client and proxy-server:
// - Each message starts with a header.
// - The first 3 bytes of the header is the connection-id the data refers to encoded as a UTF8 string.
// - The following 4 bytes of the header is the length of the message encoded as a UTF8 string.
// - The message length may not be larger than 4_096 minus header_len bytes.

public static class TcpProxy
{
    public const int HeaderLen = 3 + 4;
    public const int WrappedMessageMaxLen = 4_096 - HeaderLen;

    public static async Task WriteMessage(Socket socket, int connectionId, Memory<byte> message)
    {
        if (message.Length > 4_096 - HeaderLen)
        {
            throw new Exception($"message length {message.Length} is larger than 4_096 - {HeaderLen}");
        }
        var header = Encoding.UTF8.GetBytes($"{connectionId,3}{message.Length,4}");
        var headerAndMessage = new Memory<byte>(new byte[header.Length + message.Length]);
        header.CopyTo(headerAndMessage);
        message.CopyTo(headerAndMessage[header.Length..]);
        await socket.SendAsync(headerAndMessage);
    }

    public static async Task ReadMessage(Socket socket, Func<Memory<byte>, int, Task> onNewMessage)
    {
        var completeMessage = new Memory<byte>(new byte[4_096]);
        int messageLength = 0;
        int usedLength = 0;
        int connectionId = 0;
        await Task.Run(() => SocketHelper.ReadSocket(socket, 4_096, async (buffer) =>
            {
                try
                {
                    var bufferSeq = new ReadOnlySequence<byte>(buffer);
                    if (messageLength == 0)
                    {
                        connectionId = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(0, 3)));
                        messageLength = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(3, 4)));
                        usedLength = buffer.Length - HeaderLen;
                        Console.WriteLine($"connectionId: {connectionId}, buffer.Length: {buffer.Length}, messageLength: {messageLength}, messageLength+7: {messageLength + HeaderLen}, usedLength: {usedLength}");
                        buffer[7..].CopyTo(completeMessage);
                    }
                    else
                    {
                        var prevUsedLength = usedLength;
                        usedLength += buffer.Length;
                        Console.WriteLine($"connectionId: {connectionId}, buffer.Length: {buffer.Length}, messageLength: {messageLength}, usedLength: {usedLength}, prevUsedLength: {prevUsedLength}");
                        buffer.CopyTo(completeMessage.Slice(usedLength, buffer.Length));
                    }
                    bool isComplete = usedLength == messageLength;
                    Console.WriteLine($"isComplete: {isComplete}, usedLength: {usedLength}, messageLength: {messageLength}");
                    if (isComplete)
                    {
                        await onNewMessage(completeMessage[..messageLength], connectionId);
                        messageLength = 0;
                        usedLength = 0;
                        connectionId = 0;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Exception in ReadMessageFromProxyClient: {e}");
                }
            }));
    }
}
