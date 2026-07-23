using System.Runtime.CompilerServices;

// Lets Aip.Tests construct a TypeScriptSemanticModel directly from hand-built TsFile fixtures, so React/
// Angular/NextJs analyzers can be exercised without a real repo or ts-morph — the constructor is internal
// since production code always goes through TypeScriptLanguageEngine.BuildModelAsync instead.
[assembly: InternalsVisibleTo("Aip.Tests")]
