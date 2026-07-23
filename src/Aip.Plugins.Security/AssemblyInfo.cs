using System.Runtime.CompilerServices;

// Lets Aip.Tests exercise DependencyVulnerabilityAnalyzer's JSON-parsing methods directly, against
// captured sample `dotnet list package --vulnerable`/`npm audit` output — the process-invocation side
// isn't practically testable without live tooling and package-registry network access, but the parsing
// itself is pure and should be, hence internal (test-only) rather than public surface.
[assembly: InternalsVisibleTo("Aip.Tests")]
