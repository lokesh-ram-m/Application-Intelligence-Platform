namespace Aip.Viewer;

// Small, generic text-formatting helpers shared across the endpoint and view layers — nothing here
// knows about documents, versions, or HTTP.
internal static class Formatting
{
    internal static string ShortSha(string sha) => sha[..Math.Min(7, sha.Length)];

    internal static string Humanize(string value) =>
        string.IsNullOrEmpty(value) ? value :
        string.Join(' ', value.Split('-', '_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
