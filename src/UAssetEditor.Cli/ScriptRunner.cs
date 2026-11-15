using System.Collections.Concurrent;
using System.Text;
using UAssetEditor.Tooling;

namespace UAssetEditor.Cli;

/// <summary>
/// Runs ops files against assets opened once each. With --plan, runs many assets in parallel in
/// this one process, so the usmap is loaded once rather than once per file. The ops themselves
/// live in <see cref="ScriptOps"/>, which the MCP server runs too.
/// </summary>
internal static class ScriptRunner
{
    public static int Run(ArgReader args)
    {
        if (args.Option("plan") is { } planPath) return RunPlan(args, planPath);

        var path = args.Positional(0, "file");
        var ops = Parse(args.RequireOption("ops"));
        Console.Write(RunOne(path, ops, args));
        return 0;
    }

    /// <summary>Every line is checked before any asset is opened, so a typo stops the run before it touches anything.</summary>
    public static IReadOnlyList<ScriptOp> Parse(string opsPath)
    {
        if (!File.Exists(opsPath)) throw new ArgException($"Ops file not found: {opsPath}");
        return ScriptOps.Parse(File.ReadLines(opsPath), opsPath);
    }

    private static string RunOne(string path, IReadOnlyList<ScriptOp> ops, ArgReader args)
    {
        var output = new StringBuilder();
        var asset = AssetIo.Open(path, args);

        output.Append(ScriptOps.Run(asset, ops, args));

        if (args.Flag("save"))
        {
            AssetIo.WarnIfBackupMissing(AssetIo.Save(asset, path, backup: args.Flag("backup")), args.Diagnostics);
            output.AppendLine($"Saved {path}");
        }
        return output.ToString();
    }

    /// <summary>Plan lines are "asset&lt;TAB&gt;opsfile". Each asset's output prints as one block; one failed asset doesn't stop the rest.</summary>
    private static int RunPlan(ArgReader args, string planPath)
    {
        if (!File.Exists(planPath)) throw new ArgException($"Plan file not found: {planPath}");
        _ = AssetIo.ResolveVersion(args); // a bad --game or --version should stop the run here, not fail every asset the same way

        var parsedOps = new Dictionary<string, IReadOnlyList<ScriptOp>>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<(string Asset, IReadOnlyList<ScriptOp> Ops)>();
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(planPath))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) throw new ArgException($"{planPath}:{lineNumber}: expected \"asset<TAB>opsfile\".");
            if (!File.Exists(parts[0])) throw new ArgException($"{planPath}:{lineNumber}: asset not found: {parts[0]}");
            if (!parsedOps.TryGetValue(parts[1], out var ops)) parsedOps[parts[1]] = ops = Parse(parts[1]);
            jobs.Add((parts[0], ops));
        }

        var degree = AssetIo.ResolveJobs(args);
        var failures = new ConcurrentBag<string>();
        var consoleLock = new object();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = degree }, job =>
        {
            string block;
            try
            {
                block = $"== {job.Asset}{Environment.NewLine}{RunOne(job.Asset, job.Ops, args)}";
            }
            catch (Exception ex) when (ex is ArgException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                failures.Add(job.Asset);
                block = $"== {job.Asset}{Environment.NewLine}FAILED - {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";
            }

            lock (consoleLock) Console.Write(block);
        });

        Console.Error.WriteLine($"{jobs.Count - failures.Count}/{jobs.Count} assets done ({degree} at a time).");
        return failures.IsEmpty ? 0 : 1;
    }
}
