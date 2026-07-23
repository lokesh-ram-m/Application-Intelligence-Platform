using System.Runtime.CompilerServices;

// Lets Aip.Tests exercise the plugin's individual analyzers (ControllerAnalyzer, MinimalApiAnalyzer, ...)
// directly against a hand-built in-memory Roslyn compilation, rather than only indirectly through a real
// repository clone in an end-to-end test.
[assembly: InternalsVisibleTo("Aip.Tests")]
