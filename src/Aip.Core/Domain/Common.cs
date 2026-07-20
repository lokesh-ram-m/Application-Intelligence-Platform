namespace Aip.Core.Domain;

/// <summary>Raised when a domain invariant is violated. The domain protects itself; it never trusts its callers.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>Guard clauses used by value objects and aggregates to enforce invariants at construction.</summary>
public static class Guard
{
    public static string NotNullOrWhiteSpace(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{name} must not be null or empty.");

        return value;
    }

    public static IReadOnlyList<T> NotEmpty<T>(IReadOnlyList<T>? value, string name)
    {
        if (value is null || value.Count == 0)
            throw new DomainException($"{name} must contain at least one element.");

        return value;
    }

    public static void InRange(double value, double min, double max, string name)
    {
        if (double.IsNaN(value) || value < min || value > max)
            throw new DomainException($"{name} must be between {min} and {max} (was {value}).");
    }

    public static void Requires(bool condition, string message)
    {
        if (!condition)
            throw new DomainException(message);
    }
}

/// <summary>
/// A normalized confidence in a fact, in the closed interval [0,1]. Deterministic facts are 1.0;
/// heuristic/probabilistic facts are lower. Consumers use this to distinguish resolved facts from guesses.
/// </summary>
public readonly record struct Confidence
{
    public double Value { get; }

    public Confidence(double value)
    {
        Guard.InRange(value, 0.0, 1.0, nameof(Confidence));
        Value = value;
    }

    /// <summary>Full certainty — the confidence of a deterministic extraction.</summary>
    public static Confidence Full => new(1.0);

    public static Confidence From(double value) => new(value);

    public override string ToString() => Value.ToString("0.00");
}
