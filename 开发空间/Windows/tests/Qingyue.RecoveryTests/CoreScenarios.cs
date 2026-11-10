using System.Reflection;
using System.Text;
using System.Text.Json;
using Qingyue.Core;

internal static partial class Program
{
    private static async Task RunCoreScenarios()
    {
        await Scenario("unopened_draft_survives_empty_autosave", async root =>
        {
            var file = new DocumentFile("UNOPENED_RECOVERY_TEXT");
            string profile = Path.Combine(root, "profile");
            await Save(new WorkspaceStore(profile), Session(file), file.CaptureSnapshot());
            byte[] before = File.ReadAllBytes(Draft(profile, file.Id));
            var store = new WorkspaceStore(profile);
            await Prepare(store);
            await Save(store, new WorkspaceSession(1, [], null));
            Check(File.Exists(Draft(profile, file.Id)), "empty autosave deleted an unopened recovery draft");
            Check(before.SequenceEqual(File.ReadAllBytes(Draft(profile, file.Id))), "unopened draft bytes changed");
            Check((await new WorkspaceStore(profile).LoadAsync()).Documents.Single().File.Text == "UNOPENED_RECOVERY_TEXT",
                "unopened draft is no longer discoverable after restart");
        });
        await Scenario("unopened_draft_survives_disabled_recovery_and_new_session", async root =>
        {
            var old = new DocumentFile("OLD_UNOPENED");
            var current = new DocumentFile("CURRENT_TAB");
            string profile = Path.Combine(root, "profile");
            await Save(new WorkspaceStore(profile), Session(old), old.CaptureSnapshot());
            var store = new WorkspaceStore(profile);
            await Prepare(store);
            await Save(store, Session(current) with { Entries = [Session(current).Entries[0] with { RecoverDraft = false }] });
            await Prepare(store); // Repeated preparation must not protect newly managed drafts.
            Check(File.Exists(Draft(profile, old.Id)), "turning recovery off silently deleted old content");
            await store.ClearDraftsAsync();
            Check(!File.Exists(Draft(profile, old.Id)), "explicit clear did not remove retained unopened draft");
            await Save(store, new WorkspaceSession(1, [], null));
            Check((await new WorkspaceStore(profile).LoadAsync()).Documents.Count == 0, "explicit clear resurrected old draft");
        });
        await Scenario("opened_discard_does_not_resurrect_while_unopened_is_retained", async root =>
        {
            var old = new DocumentFile("UNOPENED");
            var opened = new DocumentFile("EXPLICITLY_DISCARDED");
            string profile = Path.Combine(root, "profile");
            await Save(new WorkspaceStore(profile), Session(old, opened), old.CaptureSnapshot(), opened.CaptureSnapshot());
            var store = new WorkspaceStore(profile);
            await Prepare(store);
            var overload = typeof(WorkspaceStore).GetMethod("SaveAsync",
                [typeof(WorkspaceSession), typeof(IReadOnlyList<DocumentSnapshot>), typeof(IReadOnlyCollection<Guid>)]);
            WorkspaceSaveResult result = overload is null
                ? await store.SaveAsync(new WorkspaceSession(1, [], null), [])
                : await (Task<WorkspaceSaveResult>)overload.Invoke(store, [new WorkspaceSession(1, [], null), Array.Empty<DocumentSnapshot>(), new[] { opened.Id }])!;
            Check(result.Errors.Count == 0, string.Join("; ", result.Errors));
            Check(!File.Exists(Draft(profile, opened.Id)), "opened discarded draft survived explicit discard");
            Check(File.Exists(Draft(profile, old.Id)), "discard of opened tab deleted a different unopened draft");
        });
        await Scenario("rename_crash_recovers_pathless_copy", async root =>
        {
            var (file, store, profile, oldPath, newPath) = await DirtyFixture(root);
            file.Rename(Path.GetFileName(newPath));
            // Materialize exactly the crash checkpoint: new draft committed, old session still durable.
            File.WriteAllText(Draft(profile, file.Id), JsonSerializer.Serialize(file.CaptureSnapshot()));
            var result = await new WorkspaceStore(profile).LoadAsync();
            Check(result.Documents.Count == 1, "path mismatch quarantined the only recoverable body");
            var recovered = result.Documents.Single();
            Check(recovered.File.Id == file.Id && recovered.File.Text == "UNSAVED_BODY", "recovery lost ID/body");
            Check(recovered.File.Path is null && recovered.Entry.Path is null && !recovered.File.CaptureSnapshot().WasSaved,
                "mismatched draft acquired authority over a disk path");
            Check(recovered.File.SuggestedName == "renamed.txt", "pathless copy lost its suggested name");
            Check(recovered.File.IsDirty && result.Warnings.Count > 0, "pathless recovery must remain dirty and visible");
            bool saveBlocked = false;
            try { recovered.File.Save(); } catch (IOException) { saveBlocked = true; }
            Check(saveBlocked, "pathless copy saved to an original path without an explicit destination");
            await Save(store, new WorkspaceSession(1, [recovered.Entry], recovered.File.Id), recovered.File.CaptureSnapshot());
            var again = await new WorkspaceStore(profile).LoadAsync();
            Check(again.Documents.Single().File.Path is null && again.Documents.Single().File.Text == "UNSAVED_BODY",
                "pathless recovery did not survive a second save/load");
            Check(!File.Exists(oldPath) && File.ReadAllText(newPath) == "BASELINE", "recovery overwrote disk baseline");
            Check(!Directory.Exists(Path.Combine(profile, "Quarantine")), "valid renamed draft was quarantined");
        });
        await Scenario("mismatched_existing_paths_never_overwrite_either_file", async root =>
        {
            var (file, _, profile, oldPath, newPath) = await DirtyFixture(root);
            File.WriteAllText(newPath, "OTHER_DISK_FILE");
            var snapshot = file.CaptureSnapshot() with { Path = newPath };
            File.WriteAllText(Draft(profile, file.Id), JsonSerializer.Serialize(snapshot));
            var load = await new WorkspaceStore(profile).LoadAsync();
            Check(load.Documents.Single().File.Path is null && load.Documents.Single().File.Text == "UNSAVED_BODY",
                "mismatched body was not recovered as a pathless copy");
            Check(File.ReadAllText(oldPath) == "BASELINE" && File.ReadAllText(newPath) == "OTHER_DISK_FILE",
                "load modified an original file");
        });
        await Scenario("catchable_session_write_failure_rolls_back_rename", async root =>
        {
            var (file, store, profile, _, newPath) = await DirtyFixture(root);
            byte[] draft = File.ReadAllBytes(Draft(profile, file.Id));
            byte[] session = File.ReadAllBytes(Path.Combine(profile, "Session.json"));
            file.Rename(Path.GetFileName(newPath));
            using (var locked = new FileStream(Path.Combine(profile, "Session.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check((await store.SaveAsync(Session(file), [file.CaptureSnapshot()])).Errors.Count > 0, "locked session did not fail");
            Check(draft.SequenceEqual(File.ReadAllBytes(Draft(profile, file.Id))), "draft rollback failed");
            Check(session.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "Session.json"))), "session rollback failed");
            Check((await store.LoadAsync()).Documents.Single().File.Text == "UNSAVED_BODY", "rollback lost body");
        });
        await Scenario("invalid_snapshot_stays_quarantined", async root =>
        {
            var (file, _, profile, _, newPath) = await DirtyFixture(root);
            var snapshot = file.CaptureSnapshot() with { Path = newPath, BaselineDigest = "invalid" };
            File.WriteAllText(Draft(profile, file.Id), JsonSerializer.Serialize(snapshot));
            var load = await new WorkspaceStore(profile).LoadAsync();
            Check(load.Documents.Single().File.Text == "BASELINE" && !load.Documents.Single().File.IsDirty,
                "invalid snapshot bypassed snapshot validation");
            Check(Directory.GetFiles(Path.Combine(profile, "Quarantine")).Length == 1, "invalid snapshot was not quarantined");
        });
        await Scenario("explicit_clear_suppresses_mismatched_draft", async root =>
        {
            var (file, store, profile, _, newPath) = await DirtyFixture(root);
            var snapshot = file.CaptureSnapshot() with { Path = newPath };
            await store.ClearDraftsAsync();
            File.WriteAllText(Draft(profile, file.Id), JsonSerializer.Serialize(snapshot));
            var load = await store.LoadAsync();
            Check(load.Documents.All(d => d.File.Text != "UNSAVED_BODY"), "explicitly suppressed draft was resurrected");
        });
    }

    private static WorkspaceSession Session(params DocumentFile[] files) =>
        new(1, files.Select(file => new WorkspaceEntry(file.Id, file.Path, false, 0, 0, 0)).ToList(), files.FirstOrDefault()?.Id);

    private static string Draft(string profile, Guid id) => Path.Combine(profile, "Drafts", id + ".json");

    private static async Task Save(WorkspaceStore store, WorkspaceSession session, params DocumentSnapshot[] drafts)
    {
        var result = await store.SaveAsync(session, drafts);
        Check(result.Errors.Count == 0, string.Join("; ", result.Errors));
    }

    private static async Task<(DocumentFile File, WorkspaceStore Store, string Profile, string OldPath, string NewPath)> DirtyFixture(string root)
    {
        string oldPath = Path.Combine(root, "original.txt"), newPath = Path.Combine(root, "renamed.txt");
        File.WriteAllText(oldPath, "BASELINE", new UTF8Encoding(false));
        var file = DocumentFile.Open(oldPath);
        file.Text = "UNSAVED_BODY";
        string profile = Path.Combine(root, "profile");
        var store = new WorkspaceStore(profile);
        await Save(store, Session(file), file.CaptureSnapshot());
        return (file, store, profile, oldPath, newPath);
    }
}
