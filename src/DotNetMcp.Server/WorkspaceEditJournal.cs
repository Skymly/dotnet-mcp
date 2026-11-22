using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotNetMcp.Server;

/// <summary>Write-ahead evidence for the shared Workspace Edit writer. All journal operations run under
/// the per-user mutex, including source writes, so another service cannot recover an active edit.</summary>
internal sealed class WorkspaceEditJournal
{
    private const int FormatVersion = 1;
    private readonly string _directory;
    private readonly TrustedRoots _roots;

    internal WorkspaceEditJournal(TrustedRoots roots, string? directory = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        _directory = directory ?? (Path.IsPathFullyQualified(local)
            ? Path.Combine(PathPolicy.Normalize(local), "Skymly", "DotNetMcp", "recovery") : "");
        _roots = roots;
    }

    internal sealed record Entry(string Path, byte[] Original, string OriginalHash, string IntendedHash)
    {
        internal static Entry From(string path, byte[] original, byte[] intended) =>
            new(path, original, Hash(original), Hash(intended));
    }

    private sealed record Record(int Version, string Workspace, Entry[] Files);

    internal IDisposable Enter()
    {
        var name = "Skymly.DotNetMcp.Apply." + Hash(Encoding.UTF8.GetBytes(_directory));
        var mutex = new Mutex(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new IOException("Another service is applying or recovering a workspace edit.");
        }
        catch (AbandonedMutexException)
        {
            // The process that owned the previous write died. The current thread owns the lock.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new Lease(mutex);
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    internal string? Begin(string workspace, Entry[] files)
    {
        var paths = files.Select(f => f.Path).ToHashSet(PathPolicy.Comparer);
        if (paths.Count != files.Length)
            throw new IOException("Two preview documents resolve to the same file.");

        foreach (var path in Pending())
        {
            var pending = Read(path);
            if (PathPolicy.Comparer.Equals(pending.Workspace, workspace) || pending.Files.Any(f => paths.Contains(f.Path)))
                throw new IOException("An earlier apply still has recovery evidence for this workspace or target. Restart the service and inspect workspace_status before retrying.");
        }

        if (files.Length < 2)
            return null;

        EnsurePrivateDirectory();
        var journalPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        var staging = journalPath + ".tmp";
        try
        {
            using (var stream = CreatePrivateFile(staging))
            {
                JsonSerializer.Serialize(stream, new Record(FormatVersion, workspace, files));
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, journalPath);
        }
        finally
        {
            File.Delete(staging);
        }
        return journalPath;
    }

    internal static void Forget(string? path)
    {
        if (path is not null)
            File.Delete(path);
    }

    internal IReadOnlyList<WorkspaceEditRecoveryDto> Recover()
    {
        try
        {
            return RecoverPending();
        }
        catch (Exception ex) when (IsEvidenceError(ex))
        {
            return [Unverified()];
        }
    }

    private static bool IsEvidenceError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;

    private static WorkspaceEditRecoveryDto Unverified() => new("conflict", null, [],
        "Recovery evidence could not be verified or finalized. Retain the journal and check permissions and server version.");

    private IReadOnlyList<WorkspaceEditRecoveryDto> RecoverPending()
    {
        if (!Pending().Any())
            return [];

        using var lease = Enter();
        var results = new List<WorkspaceEditRecoveryDto>();
        foreach (var path in Pending())
        {
            try
            {
                var record = Read(path);
                if (!IsOriginalPath(record.Workspace))
                    continue;
                var result = Recover(record);
                if (result.State != "conflict")
                    Forget(path);
                results.Add(result);
            }
            catch (Exception ex) when (IsEvidenceError(ex))
            {
                results.Add(Unverified());
            }
        }
        return results;
    }

    private WorkspaceEditRecoveryDto Recover(Record record)
    {
        var conflicts = new List<string>();
        var changed = new List<Entry>();
        var unauthorized = false;
        var allIntended = true;
        foreach (var file in record.Files)
        {
            // Never follow a retargeted path, even if the replacement is also inside a trusted root.
            if (!IsOriginalPath(file.Path))
            {
                unauthorized = true;
                continue;
            }
            try
            {
                var bytes = File.ReadAllBytes(file.Path);
                if (bytes.AsSpan().SequenceEqual(file.Original))
                {
                    allIntended &= file.OriginalHash == file.IntendedHash;
                    continue;
                }
                if (Hash(bytes) == file.IntendedHash)
                    changed.Add(file);
                else
                    conflicts.Add(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                conflicts.Add(file.Path);
            }
        }

        if (unauthorized || conflicts.Count > 0)
            return new("conflict", record.Workspace, conflicts,
                unauthorized ? "A target is outside current authority or was redirected. Restore the original paths and roots, then restart."
                             : "Preserve your edits. Restore the conflicting paths to their original or intended bytes, then restart.");

        if (allIntended)
            return new("completed", record.Workspace, [], "All targets already contain the intended bytes.");

        foreach (var file in changed)
        {
            try
            {
                ReplaceVerified(file.Path, file.IntendedHash, file.Original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new("conflict", record.Workspace, IsOriginalPath(file.Path) ? [file.Path] : [],
                    "Recovery stopped because a target changed or could not be restored. Resolve the conflict and restart.");
            }
        }
        return new("restored", record.Workspace, [], "All targets contain their original bytes.");
    }

    private Record Read(string path)
    {
        if (!PathPolicy.Comparer.Equals(path, PathPolicy.Normalize(path)))
            throw new InvalidDataException("The workspace edit recovery record was redirected.");
        using var stream = File.OpenRead(path);
        var record = JsonSerializer.Deserialize<Record>(stream);
        if (record is not { Version: FormatVersion, Workspace: not null, Files.Length: > 1 }
            || !Path.IsPathFullyQualified(record.Workspace)
            || record.Files.Any(f => f is not { Path: not null, Original: not null, IntendedHash.Length: 64 }
                || !Path.IsPathFullyQualified(f.Path) || Hash(f.Original) != f.OriginalHash)
            || record.Files.Select(f => f.Path).Distinct(PathPolicy.Comparer).Count() != record.Files.Length)
            throw new InvalidDataException("Unsupported or damaged workspace edit recovery evidence.");
        return record;
    }

    private IEnumerable<string> Pending()
    {
        if (string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory))
            return [];
        ValidateDirectory();
        return Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
    }

    private void EnsurePrivateDirectory()
    {
        ValidateDirectory();
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(_directory);
        else
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal bool MatchesOriginal(Entry entry)
    {
        try
        {
            return IsOriginalPath(entry.Path) && File.ReadAllBytes(entry.Path).AsSpan().SequenceEqual(entry.Original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ValidateDirectory()
    {
        if (!Path.IsPathFullyQualified(_directory) || !PathPolicy.Comparer.Equals(_directory, PathPolicy.Normalize(_directory)))
            throw new IOException("Workspace Edit recovery requires a user-local directory that has not been redirected.");
        if (!OperatingSystem.IsWindows() && Directory.Exists(_directory)
            && (File.GetUnixFileMode(_directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new IOException("Workspace Edit recovery requires a directory writable only by the current user.");
    }

    private bool IsOriginalPath(string path)
    {
        try
        {
            return _roots.ContainsNormalized(path) && PathPolicy.Comparer.Equals(path, PathPolicy.Normalize(path));
        }
        catch (PathPolicyException)
        {
            return false;
        }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    internal void ReplaceVerified(string path, string expectedHash, byte[] bytes)
    {
        if (!IsOriginalPath(path))
            throw new IOException("A workspace edit target is outside current authority or was redirected.");
        // Replace only an existing file. A process death leaves either complete version at the target.
        var staged = Path.Combine(Path.GetDirectoryName(path)!, ".dotnet-mcp-" + Guid.NewGuid().ToString("N") + ".tmp");
        var stream = CreatePrivateFile(staged);
        try
        {
            using (stream)
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!IsOriginalPath(path) || Hash(File.ReadAllBytes(path)) != expectedHash)
                throw new IOException("A workspace edit target changed before replacement.");
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("A workspace edit target is read-only.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, File.GetUnixFileMode(path));
            File.Replace(staged, path, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
