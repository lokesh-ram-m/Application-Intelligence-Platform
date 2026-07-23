using System.Runtime.CompilerServices;

// Lets Aip.Tests construct a RoslynSemanticModel directly from an in-memory CSharpCompilation, so
// individual Aip.Plugins.* analyzers can be exercised with real Roslyn semantic analysis (interface
// implementation, symbol resolution) without needing a full MSBuildWorkspace project load.
[assembly: InternalsVisibleTo("Aip.Tests")]
