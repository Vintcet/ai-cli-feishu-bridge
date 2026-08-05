using System.Text.Json;
using AiCliFeishu.Bridge.Replay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Replay.Tests;

[TestClass]
public sealed class BehaviorReplayEngineTests
{
    [TestMethod]
    public void AllSharedGoldenExamplesMatch()
    {
        var engine = new BehaviorReplayEngine();

        foreach (var file in Directory.GetFiles(ExamplesDirectory(), "*.jsonl"))
        {
            var result = engine.ReplayFile(file);

            Assert.IsTrue(result.IsSuccess, Describe(file, result));
            Assert.AreEqual(1, result.Total, file);
            Assert.AreEqual(1, result.Matched, file);
        }
    }

    [TestMethod]
    public void ProjectionDifferenceReportsRecordAndExactPath()
    {
        var line = File.ReadAllText(Path.Combine(ExamplesDirectory(), "prompt.jsonl"));
        line = line.Replace(
            "\"decision\":\"queue\"}}}",
            "\"decision\":\"steer\"}}}",
            StringComparison.Ordinal);

        var result = new BehaviorReplayEngine().Replay(new StringReader(line));

        Assert.AreEqual(1, result.Mismatched);
        Assert.AreEqual(0, result.Invalid);
        Assert.IsTrue(result.Differences.Any(item =>
            item.RecordId == "sample-prompt" &&
            item.Path == "$.observed.decision"));
    }

    [TestMethod]
    public void InvalidJsonAndWrongVersionAreReportedWithoutStoppingReplay()
    {
        var valid = File.ReadAllText(Path.Combine(ExamplesDirectory(), "approval.jsonl"));
        var wrongVersion = valid.Replace(
            "\"recordVersion\":1",
            "\"recordVersion\":2",
            StringComparison.Ordinal);
        var input = $"not-json{Environment.NewLine}{wrongVersion}{valid}";

        var result = new BehaviorReplayEngine().Replay(new StringReader(input));

        Assert.AreEqual(3, result.Total);
        Assert.AreEqual(1, result.Matched);
        Assert.AreEqual(2, result.Invalid);
        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void ReplayIsReadOnly()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "behavior.jsonl");
            File.Copy(Path.Combine(ExamplesDirectory(), "retry.jsonl"), inputPath);
            var before = Directory.GetFiles(directory)
                .ToDictionary(
                    file => Path.GetFileName(file)!,
                    file => (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)));

            var result = new BehaviorReplayEngine().ReplayFile(inputPath);

            var after = Directory.GetFiles(directory)
                .ToDictionary(
                    file => Path.GetFileName(file)!,
                    file => (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)));
            Assert.IsTrue(result.IsSuccess);
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
            foreach (var name in before.Keys)
            {
                Assert.AreEqual(before[name], after[name], name);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ExamplesDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "MigrationExamples");
    }

    private static string Describe(string file, BehaviorReplayResult result)
    {
        return $"{Path.GetFileName(file)}: {JsonSerializer.Serialize(result)}";
    }
}
