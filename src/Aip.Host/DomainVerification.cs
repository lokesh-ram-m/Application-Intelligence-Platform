using Aip.Core.Domain;

namespace Aip.Host;

/// <summary>
/// A lightweight, dependency-free exercise of the Core Domain — proves the model behaves (identity,
/// invariants, lifecycle), not just that it compiles. Invoked from the host with the "verify" arg.
/// This is verification/demonstration only; it contains no platform logic.
/// </summary>
internal static class DomainVerification
{
    public static int Run()
    {
        int passed = 0, failed = 0;

        void Check(string name, Action act)
        {
            try { act(); Console.WriteLine($"  PASS  {name}"); passed++; }
            catch (Exception ex) { Console.WriteLine($"  FAIL  {name} :: {ex.Message}"); failed++; }
        }

        void Throws<TException>(string name, Action act) where TException : Exception
        {
            try { act(); Console.WriteLine($"  FAIL  {name} :: expected {typeof(TException).Name}"); failed++; }
            catch (TException) { Console.WriteLine($"  PASS  {name}"); passed++; }
        }

        Console.WriteLine("Core Domain verification");
        Console.WriteLine();

        // --- Identity: hierarchical, deterministic, path-encoded ---
        var app = new ApplicationId("TaskFlow");
        KnowledgeIdentity typeId = KnowledgeIdentity.ForApplication(app)
            .Append(new IdentitySegment("repo", "backend"))
            .Append(new IdentitySegment("project", "Task.Api"))
            .Append(new IdentitySegment("type", "Task.Api.TaskController"));

        Check("identity encodes the ownership path", () =>
            Guard.Requires(typeId.Value == "node://app:TaskFlow/repo:backend/project:Task.Api/type:Task.Api.TaskController",
                $"unexpected identity: {typeId.Value}"));

        Check("identity round-trips through Parse", () =>
            Guard.Requires(KnowledgeIdentity.Parse(typeId.Value).Equals(typeId), "parse != original"));

        Check("same coordinate yields equal identity", () =>
            Guard.Requires(
                KnowledgeIdentity.ForApplication(app).Append(new IdentitySegment("repo", "backend"))
                    == KnowledgeIdentity.ForApplication(app).Append(new IdentitySegment("repo", "backend")),
                "identities not equal"));

        // --- Evidence + Knowledge node ---
        Evidence evidence = Evidence.Create(
            new RepositoryId("backend"), new Commit("a1b2c3d"), engine: "roslyn",
            ExtractionMethod.Deterministic, Confidence.Full,
            SourceLocation.Create("src/TaskController.cs", 42, "TaskController"));

        KnowledgeNode node = KnowledgeNode.Create(typeId, NodeKind.From("Type"), new[] { evidence }, Confidence.Full);
        Check("knowledge node created with evidence", () => Guard.Requires(node.Evidence.Count == 1, "no evidence"));

        // --- Invariant: every fact must have Evidence ---
        Throws<DomainException>("node without evidence is rejected", () =>
            KnowledgeNode.Create(typeId, NodeKind.From("Type"), Array.Empty<Evidence>(), Confidence.Full));

        // --- Snapshot aggregate invariant: edges must reference known nodes ---
        KnowledgeIdentity endpointId = KnowledgeIdentity.ForApplication(app).Append(new IdentitySegment("endpoint", "GET /api/tasks"));
        KnowledgeNode endpoint = KnowledgeNode.Create(endpointId, NodeKind.From("Endpoint"), new[] { evidence }, Confidence.Full);
        Relationship exposes = Relationship.Create(RelationshipType.From("EXPOSES"), typeId, endpointId, new[] { evidence }, Confidence.Full);

        Check("valid snapshot is accepted", () =>
            Snapshot.Create(SnapshotId.New(), app, DateTimeOffset.UtcNow, new[] { node, endpoint }, new[] { exposes }));

        Throws<DomainException>("snapshot with dangling edge is rejected", () =>
            Snapshot.Create(SnapshotId.New(), app, DateTimeOffset.UtcNow, new[] { node }, new[] { exposes }));

        // --- Execution aggregate lifecycle ---
        var execution = AnalysisExecution.Start(ExecutionId.New(), app, ExecutionMode.Local, DateTimeOffset.UtcNow);
        execution.Report(Diagnostic.Warning("unsupported project skipped", "artifact-discovery"));
        execution.Complete(ExecutionOutcome.Success, SnapshotId.New(), ExecutionMetrics.Empty, DateTimeOffset.UtcNow);
        Check("execution reaches Completed", () => Guard.Requires(execution.State == ExecutionState.Completed, "not completed"));
        Throws<DomainException>("finished execution rejects further diagnostics", () =>
            execution.Report(Diagnostic.Info("late", "x")));

        Console.WriteLine();
        Console.WriteLine($"Domain verification: {passed} passed, {failed} failed.");

        return failed == 0 ? 0 : 1;
    }
}
