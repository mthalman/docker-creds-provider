namespace Valleysoft.DockerCredsProvider;

/// <summary>
/// The exception thrown when credentials cannot be resolved from Docker
/// configuration or its credential helper.
/// </summary>
public class CredsNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CredsNotFoundException"/>
    /// class.
    /// </summary>
    public CredsNotFoundException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CredsNotFoundException"/>
    /// class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CredsNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CredsNotFoundException"/>
    /// class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that caused this exception.</param>
    public CredsNotFoundException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
