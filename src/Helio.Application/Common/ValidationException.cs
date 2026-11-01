namespace Helio.Application;

/// <summary>Raised for caller mistakes in any use case; mapped to HTTP 400 by the API.</summary>
public sealed class ValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
