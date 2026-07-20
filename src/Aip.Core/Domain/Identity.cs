namespace Aip.Core.Domain;

// Stable, hierarchical identity (Knowledge Model, Session 2 §3). Identity is deterministic and
// encodes the ownership path, so the same real-world object always resolves to the same node.

/// <summary>Registry-assigned, rename-stable key for an application.</summary>
public readonly record struct ApplicationId
{
    public string Value { get; }

    public ApplicationId(string value) => Value = Guard.NotNullOrWhiteSpace(value, nameof(ApplicationId));

    public override string ToString() => Value;
}

/// <summary>Registry-assigned, rename-stable key for a repository.</summary>
public readonly record struct RepositoryId
{
    public string Value { get; }

    public RepositoryId(string value) => Value = Guard.NotNullOrWhiteSpace(value, nameof(RepositoryId));

    public override string ToString() => Value;
}

/// <summary>Identity of a single knowledge snapshot (a labeled cut of the graph).</summary>
public readonly record struct SnapshotId
{
    public Guid Value { get; }

    public SnapshotId(Guid value)
    {
        Guard.Requires(value != Guid.Empty, "SnapshotId must not be empty.");
        Value = value;
    }

    public static SnapshotId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>Identity of a single analysis execution.</summary>
public readonly record struct ExecutionId
{
    public Guid Value { get; }

    public ExecutionId(Guid value)
    {
        Guard.Requires(value != Guid.Empty, "ExecutionId must not be empty.");
        Value = value;
    }

    public static ExecutionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// One kinded segment of a hierarchical identity, e.g. <c>project:Task.Api</c>. The kind names the
/// level in the ownership hierarchy; the value names the thing at that level.
/// </summary>
public readonly record struct IdentitySegment
{
    public string Kind { get; }
    public string Value { get; }

    public IdentitySegment(string kind, string value)
    {
        Kind = Guard.NotNullOrWhiteSpace(kind, nameof(kind));
        Value = Guard.NotNullOrWhiteSpace(value, nameof(value));
        // The kind names a hierarchy level and stays simple; the value may carry routes, generics,
        // signatures — so '/' (the URN separator) and '%' are percent-encoded in the canonical form.
        Guard.Requires(!kind.Contains(':') && !kind.Contains('/'), "Segment kind must not contain ':' or '/'.");
    }

    // Convenience factory matching the (kind, value) call shape every engine/plugin analyzer uses — the
    // single canonical spot instead of each project reimplementing this one-liner.
    public static IdentitySegment Seg(string kind, string value) => new(kind, value);

    public static IdentitySegment Parse(string text)
    {
        Guard.NotNullOrWhiteSpace(text, nameof(text));
        int i = text.IndexOf(':');
        Guard.Requires(i > 0 && i < text.Length - 1, $"Malformed identity segment '{text}'.");

        return new IdentitySegment(text[..i], Decode(text[(i + 1)..]));
    }

    public override string ToString() => $"{Kind}:{Encode(Value)}";

    // '%' first so an original '%' never collides with an encoded separator; reverse order to decode.
    private static string Encode(string value) => value.Replace("%", "%25").Replace("/", "%2F");
    private static string Decode(string value) => value.Replace("%2F", "/").Replace("%25", "%");
}

/// <summary>
/// The deterministic, hierarchical identity of a Knowledge Node, e.g.
/// <c>node://app:TaskFlow/repo:backend/project:Task.Api/type:Task.Api.TaskController</c>.
/// Equality is by the canonical string, so the same coordinate always compares equal.
/// The file path is deliberately NOT part of identity — that lives in Evidence.
/// </summary>
public readonly record struct KnowledgeIdentity
{
    public const string DefaultScheme = "node";

    /// <summary>The canonical URN. This is the equality key.</summary>
    public string Value { get; }

    private KnowledgeIdentity(string canonical) => Value = canonical;

    public static KnowledgeIdentity Create(IReadOnlyList<IdentitySegment> segments, string scheme = DefaultScheme)
    {
        Guard.NotNullOrWhiteSpace(scheme, nameof(scheme));
        Guard.NotEmpty(segments, nameof(segments));

        return new KnowledgeIdentity($"{scheme}://{string.Join('/', segments)}");
    }

    public static KnowledgeIdentity Parse(string value)
    {
        (string scheme, IReadOnlyList<IdentitySegment> segments) = Split(value);

        return Create(segments, scheme);
    }

    /// <summary>Root identity for an application — the top of every hierarchy.</summary>
    public static KnowledgeIdentity ForApplication(ApplicationId application) =>
        Create(new[] { new IdentitySegment("app", application.Value) });

    public IReadOnlyList<IdentitySegment> Segments => Split(Value).Segments;

    /// <summary>
    /// The last segment's value — a compact, human-readable label for diagnostics and summaries (e.g.
    /// "TaskController" rather than the full "node://app:TaskFlow/repo:backend/.../type:...TaskController"
    /// URN). Falls back to the full canonical value on the rare identity with no segments.
    /// </summary>
    public string ShortName
    {
        get
        {
            IReadOnlyList<IdentitySegment> segments = Segments;

            return segments.Count == 0 ? Value : segments[^1].Value;
        }
    }

    /// <summary>Derive a child identity by appending a segment (deterministic descent down the hierarchy).</summary>
    public KnowledgeIdentity Append(IdentitySegment segment)
    {
        (string scheme, IReadOnlyList<IdentitySegment> segments) = Split(Value);
        var next = new List<IdentitySegment>(segments) { segment };

        return Create(next, scheme);
    }

    private static (string Scheme, IReadOnlyList<IdentitySegment> Segments) Split(string value)
    {
        Guard.NotNullOrWhiteSpace(value, nameof(value));
        int sep = value.IndexOf("://", StringComparison.Ordinal);
        Guard.Requires(sep > 0, $"Malformed identity '{value}'. Expected '<scheme>://<segment>/...'.");
        string scheme = value[..sep];
        string rest = value[(sep + 3)..];
        Guard.NotNullOrWhiteSpace(rest, "identity body");
        IReadOnlyList<IdentitySegment> segments = rest
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(IdentitySegment.Parse)
            .ToList();

        return (scheme, segments);
    }

    public override string ToString() => Value;
}

/// <summary>
/// Builds a properties dictionary from (key, value) pairs — shared by every analyzer across every engine
/// and plugin that attaches ad-hoc properties to a node/relationship discovery.
/// </summary>
public static class PropertyBag
{
    public static Dictionary<string, string> Props(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach ((string k, string v) in pairs) d[k] = v;

        return d;
    }
}
