using Xunit;

// Several tests here (EndToEndTests, ExecutionPipelineIncrementalTests) run real MSBuildWorkspace
// restores/analysis against the same physical samples/backend directory — xUnit's default behavior runs
// different test classes as separate collections IN PARALLEL, which lets two of these race on the same
// obj/bin/project.assets.json files at once. That's a real bug, not just slowness: it produces genuine
// MSBuild "child node exited prematurely" failures and file-lock contention that can stall a single test
// for many minutes. Disabling collection parallelization serializes everything in this assembly, which is
// the correct trade-off here — every test touches a shared, stateful resource (a real repo on disk, a
// dedicated SQL Server database, or both), so nothing in this suite is actually safe to run concurrently
// with anything else in it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
