using System;

namespace Bendover.Domain.Exceptions;

public class DockerUnavailableException : Exception
{
    public DockerUnavailableException(string message) : base(message)
    {
    }

    public DockerUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
