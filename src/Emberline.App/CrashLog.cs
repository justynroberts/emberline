using Emberline.Core.Storage;

namespace Emberline.App;

/// <summary>
/// Records what went wrong when something goes wrong badly enough to matter.
///
/// The application had no crash logging at all. An unhandled exception aborted
/// the process and left nothing behind but a system report full of unsymbolicated
/// frames — enough to know it died, not enough to know why. For a program that
/// drives a laser that is not good enough: the first thing anybody asks after a
/// crash is what it was doing, and the answer has to be written down somewhere at
/// the moment it happens.
/// </summary>
public static class CrashLog
{
    public static string Folder => Path.Combine(AppPaths.Root, "logs");

    /// <summary>
    /// Write one report. Never throws: this runs while the process is already
    /// failing, and an exception here would replace a diagnosable crash with a
    /// mysterious one.
    /// </summary>
    public static string? Write(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyy-MM-dd-HHmmss-fff}.log");
            var text =
                $"""
                 Emberline crash report
                 =====================
                 When:     {DateTimeOffset.Now:u}
                 Where:    {context}
                 Platform: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})
                 Runtime:  {Environment.Version}

                 {Describe(exception)}
                 """;

            File.WriteAllText(path, text);
            Prune();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Type, message and stack, following inner exceptions all the way down.</summary>
    private static string Describe(Exception? exception, int depth = 0)
    {
        if (exception is null) return "(no exception)";

        var indent = new string(' ', depth * 2);
        var text =
            $"{indent}{exception.GetType().FullName}: {exception.Message}\n" +
            $"{indent}{exception.StackTrace}\n";

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                text += $"\n{indent}--- inner ---\n{Describe(inner, depth + 1)}";
            }
            return text;
        }

        return exception.InnerException is null
            ? text
            : text + $"\n{indent}--- caused by ---\n{Describe(exception.InnerException, depth + 1)}";
    }

    /// <summary>Keep the last twenty. A crash log folder that grows for ever is its own problem.</summary>
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("crash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(20);

            foreach (var file in files) file.Delete();
        }
        catch (Exception)
        {
            // Tidying is not worth failing over.
        }
    }
}
