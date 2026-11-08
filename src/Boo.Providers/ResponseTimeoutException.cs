namespace Boo.Providers;

public sealed class ResponseTimeoutException : TimeoutException
{
    public ResponseTimeoutException() : base("OpenAI response timed out. Try again.")
    {
    }
}
