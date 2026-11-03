using System.Text.Json;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>
/// Moves the models to another folder when the user picks one: only with enough room and with the engine stopped, resumable, and
/// an original is only deleted once every copy has its full size. Times are kept: a pack checks them. Between drives the folder in
/// the settings changes after the copies are whole, so at every moment one complete set is in use; on one drive files are renamed.
/// </summary>
public sealed class ModelFolderMover
{
    public sealed record Check(ulong Needed, ulong Free, bool SameDrive, string? Problem);
    public sealed record Move(string From, string To);

    private const int Chunk = 1 << 20;
    private static string PendingFile => Path.Combine(Paths.AppData, "models-move.json");

    /// <summary>What a move from the folder in use to `to` asks, and what stands in its way.</summary>
    public static Check Prepare(string from, string to, bool engineRunning, Func<string, DriveReport>? driveOf = null)
    {
        if (engineRunning) return new Check(0, 0, false, L("Stop eerst de engine; daarna kunnen de modellen worden verplaatst."));
        if (!Directory.Exists(from)) return new Check(0, 0, false, L($"De map {from} staat niet (meer) op deze pc. Er is niets verplaatst en niets verwijderd."));
        var fromFull = Normal(from);
        var toFull = Normal(to);
        if (string.Equals(fromFull, toFull, StringComparison.OrdinalIgnoreCase)) return new Check(0, 0, true, L("Dit is de map die al in gebruik is."));
        if (toFull.StartsWith(fromFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new Check(0, 0, false, L("De nieuwe map ligt in de huidige modelmap. Kies een map erbuiten."));
        var drive = (driveOf ?? DriveReport.For)(toFull);
        if (!drive.Ready) return new Check(0, 0, false, L($"De schijf {drive.Root} is niet beschikbaar."));
        bool same = string.Equals(Path.GetPathRoot(fromFull), Path.GetPathRoot(toFull), StringComparison.OrdinalIgnoreCase);
        ulong needed = 0;
        foreach (var file in Files(fromFull))
        {
            var target = Path.Combine(toFull, Path.GetRelativePath(fromFull, file));
            ulong size = ModelStore.FileSize(file), there = ModelStore.FileSize(target);
            if (there > size) return new Check(0, drive.FreeBytes, same, L($"{target} bestaat al en is groter dan het origineel. Er is niets verplaatst."));
            needed += size - there;
        }
        if (!same && needed > drive.FreeBytes)
            return new Check(needed, drive.FreeBytes, same, L($"Op {drive.Root} is {drive.FreeBytes.Gb()} vrij; de modellen hebben {needed.Gb()} nodig. Er is niets verplaatst."));
        return new Check(needed, drive.FreeBytes, same, null);
    }

    /// <summary>A move the app was closed in the middle of, to offer again at start.</summary>
    public static Move? Pending()
    {
        try { return File.Exists(PendingFile) ? JsonSerializer.Deserialize<Move>(File.ReadAllText(PendingFile)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IEnumerable<string> Files(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : Array.Empty<string>();

    /// <summary>Moves everything from the folder in use to `to` and makes that the model folder. On return null, or what went wrong;
    /// after an error or a stop nothing is lost and a next call goes on where this one ended.</summary>
    public static async Task<string?> Run(string to, bool engineRunning, Action<ulong, ulong>? progress = null, CancellationToken ct = default)
    {
        string from = Normal(Paths.ModelsRoot);
        string target = Normal(to);
        var check = Prepare(from, target, engineRunning);
        if (check.Problem != null) return check.Problem;
        try
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(PendingFile, JsonSerializer.Serialize(new Move(from, target)));
            var files = Files(from).ToList();
            ulong total = files.Aggregate(0UL, (n, f) => n + ModelStore.FileSize(f)), done = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(target, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                ulong size = ModelStore.FileSize(file);
                if (check.SameDrive && !File.Exists(dest))
                {
                    // the same drive: a rename, nothing to copy or to verify
                    File.Move(file, dest);
                }
                else
                {
                    await CopyRest(file, dest, size, n => progress?.Invoke(done + n, total), ct);
                }
                done += size;
                progress?.Invoke(done, total);
            }

            // only now: every file there at its full size
            foreach (var file in Files(from).ToList())
            {
                var dest = Path.Combine(target, Path.GetRelativePath(from, file));
                if (ModelStore.FileSize(dest) != ModelStore.FileSize(file))
                    return L($"{dest} heeft niet de juiste grootte. Het origineel blijft bestaan.");
            }
            Settings.Shared.ModelFolder = target == Normal(Paths.DefaultModelsRoot) ? "" : target;
            foreach (var file in Files(from).ToList()) File.Delete(file);
            RemoveEmptyFolders(from);
            File.Delete(PendingFile);
            return null;
        }
        catch (OperationCanceledException) { return L("Verplaatsen gestopt. De originelen staan er nog; verplaatsen gaat later verder waar het bleef."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return L($"Verplaatsen mislukt: {e.Message}. De originelen staan er nog.");
        }
    }

    /// <summary>Goes on after what is already at the destination, and keeps the file's time.</summary>
    private static async Task CopyRest(string file, string dest, ulong size, Action<ulong> progress, CancellationToken ct)
    {
        ulong there = ModelStore.FileSize(dest);
        if (there < size)
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var output = new FileStream(dest, FileMode.Append, FileAccess.Write, FileShare.Read, Chunk, FileOptions.Asynchronous);
            input.Position = (long)there;
            var buffer = new byte[Chunk];
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                there += (ulong)n;
                progress(there);
            }
        }
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
    }

    private static void RemoveEmptyFolders(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
