using System.Runtime.CompilerServices;

// Lets Aip.Tests exercise the plugin's individual analyzers (AngularComponentAnalyzer, AngularGuardAnalyzer,
// ...) directly against a hand-built TypeScriptSemanticModel, rather than only indirectly through a real
// repository clone in an end-to-end test.
[assembly: InternalsVisibleTo("Aip.Tests")]
