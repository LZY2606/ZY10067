namespace MicroFocus.Core.Storage;

public sealed class ConflictException : Exception
{
    public required long CurrentRevision { get; init; }
    public ConflictException(string message) : base(message) { }
}

public sealed class DomainException : Exception
{
    public string Code { get; }
    public DomainException(string message) : base(message) => Code = "DOMAIN_ERROR";
    public DomainException(string code, string message) : base(message) => Code = code;
}
