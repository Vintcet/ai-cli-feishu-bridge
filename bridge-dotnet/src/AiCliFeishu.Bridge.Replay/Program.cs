using AiCliFeishu.Bridge.Replay;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: AiCliFeishu.Bridge.Replay <behavior-record.jsonl>");
    return 2;
}

try
{
    var result = new BehaviorReplayEngine().ReplayFile(args[0]);
    Console.WriteLine(
        $"replay total={result.Total} matched={result.Matched} mismatched={result.Mismatched} invalid={result.Invalid}");
    foreach (var difference in result.Differences)
    {
        Console.Error.WriteLine(
            $"line={difference.LineNumber} record={difference.RecordId} path={difference.Path}: {difference.Message}");
    }
    return result.IsSuccess ? 0 : 1;
}
catch (Exception error) when (
    error is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"Replay could not read input: {error.Message}");
    return 2;
}
