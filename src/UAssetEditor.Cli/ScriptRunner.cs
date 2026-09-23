using System.Collections.Concurrent;
using System.Text;
using UAssetAPI;

namespace UAssetEditor.Cli;

/// <summary>One parsed line of an ops file.</summary>
internal sealed record ScriptOp(int Line, string Verb, ArgReader Args);

/// <summary>
/// Runs ops files against assets opened once each. With --plan, runs many assets in parallel in
/// this one process, so the usmap is loaded once rather than once per file.
/// </summary>
internal static class ScriptRunner
{
    private static readonly Dictionary<string, Func<UAsset, ArgReader, string>> Ops = new(StringComparer.Ordinal)
    {
        ["set"] = Commands.ApplySet,
        ["duplicate"] = Commands.ApplyDuplicate,
        ["remove"] = Commands.ApplyRemove,
        ["add-node"] = Commands.ApplyAddNode,
        ["splice-node"] = Commands.ApplySpliceNode,
        ["append-clone"] = Commands.ApplyAppendClone,
        ["duplicate-export"] = Commands.ApplyDuplicateExport,
        ["dump"] = Commands.ApplyDump,
    };

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

        var ops = new List<ScriptOp>();
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(opsPath))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = Tokenize(line);
            if (!Ops.ContainsKey(tokens[0]))
                throw new ArgException($"{opsPath}:{lineNumber}: unknown op '{tokens[0]}' (expects {string.Join('/', Ops.Keys)}).");
            try
            {
                ops.Add(new ScriptOp(lineNumber, tokens[0], new ArgReader(tokens.Skip(1))));
            }
            catch (ArgException ex)
            {
                throw new ArgException($"{opsPath}:{lineNumber}: {ex.Message}", ex);
            }
        }
        return ops;
    }

    private static string RunOne(string path, IReadOnlyList<ScriptOp> ops, ArgReader args)
    {
        var output = new StringBuilder();
        var asset = AssetIo.Open(path, args);

        foreach (var op in ops)
        {
            try
            {
                var result = Ops[op.Verb](asset, op.Args);
                if (op.Verb == "dump") output.Append(result);
                else output.AppendLine($"[{op.Line}] {op.Verb}: {result}");
            }
            catch (Exception ex) when (ex is ArgException or InvalidOperationException)
            {
                output.AppendLine($"[{op.Line}] {op.Verb}: SKIPPED - {ex.Message}");
            }
        }

        if (args.Flag("save"))
        {
            AssetIo.Save(asset, path, backup: args.Flag("backup"));
            output.AppendLine($"Saved {path}");
        }
        return output.ToString();
    }

    /// <summary>Plan lines are "asset&lt;TAB&gt;opsfile". Each asset's output prints as one block; one failed asset doesn't stop the rest.</summary>
    private static int RunPlan(ArgReader args, string planPath)
    {
        if (!File.Exists(planPath)) throw new ArgException($"Plan file not found: {planPath}");

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

        var degree = ResolveJobs(args);
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

    private static int ResolveJobs(ArgReader args)
    {
        if (args.Option("jobs") is not { } text) return Environment.ProcessorCount;
        return int.TryParse(text, out var jobs) && jobs > 0 ? jobs : throw new ArgException($"--jobs must be a positive number, not '{text}'.");
    }

    /// <summary>Splits one script line into tokens, honoring "double-quoted segments" so a --value can contain spaces.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        return tokens;
    }
}
