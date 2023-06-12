namespace proxy_common;

public interface ILogger
{
    void Info(string message);
    void Error(string message);
}

public class Logger : ILogger
{
    public void Info(string message)
    {
        Console.WriteLine($"{DateTime.UtcNow:yyy-MM-dd HH:mm:ss.fff} {message}");
    }

    public void Error(string message)
    {
        Console.WriteLine($"{DateTime.UtcNow:yyy-MM-dd HH:mm:ss.fff} {message}");
    }
}