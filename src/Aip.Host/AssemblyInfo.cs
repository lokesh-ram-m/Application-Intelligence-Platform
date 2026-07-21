using System.Runtime.CompilerServices;

// Lets Aip.Tests exercise AppsFile's validation and PlatformRunner's topological ordering directly — both
// are internal (no public entry point exists for either in isolation; RunBatchAsync wraps both together)
// and neither is part of the platform's public surface, so this is test-only, compile-time visibility.
[assembly: InternalsVisibleTo("Aip.Tests")]
