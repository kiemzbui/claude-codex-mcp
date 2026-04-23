using System.Collections.Generic;
using System.Linq;

namespace ClaudeCodexMcp.Domain;

public sealed record PolicyValidationError(string Code, string Message, string? Field = null);

public sealed class PolicyValidationResult<T>
{
    private PolicyValidationResult(T? value, IReadOnlyList<PolicyValidationError> errors)
    {
        Value = value;
        Errors = errors;
    }

    public T? Value { get; }

    public IReadOnlyList<PolicyValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static PolicyValidationResult<T> Success(T value) => new(value, []);

    public static PolicyValidationResult<T> Failure(IEnumerable<PolicyValidationError> errors) =>
        new(default, errors.ToArray());
}
