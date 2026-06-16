namespace TechSupportBatchSubmitter.Core.Exceptions;

public class PlatformException : Exception
{
    public PlatformException(string message) : base(message)
    {
    }

    public PlatformException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class PlatformSessionExpiredException : PlatformException
{
    public PlatformSessionExpiredException(string message) : base(message)
    {
    }
}

public sealed class PlatformProtocolException : PlatformException
{
    public PlatformProtocolException(string message) : base(message)
    {
    }

    public PlatformProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class SubmissionOutcomeUnknownException : PlatformException
{
    public SubmissionOutcomeUnknownException(string message, Exception? innerException = null)
        : base(message, innerException ?? new InvalidOperationException(message))
    {
    }
}
