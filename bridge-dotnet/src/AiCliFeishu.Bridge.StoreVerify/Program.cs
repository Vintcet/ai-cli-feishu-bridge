using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Adapters.Storage;

if (args.Length is < 1 or > 3 || (args.Length == 3 && args[1] != "--roundtrip-copy"))
{
    Console.Error.WriteLine(
        "用法: AiCliFeishu.Bridge.StoreVerify <Node data 目录> " +
        "[--roundtrip-copy <空的副本目录>]");
    return 2;
}

try
{
    var sourcePath = Path.GetFullPath(args[0]);
    var source = new NodeJsonStoreRepository(sourcePath);
    var snapshot = await source.LoadAsync();
    var core = NodeStoreCoreProjection.Project(snapshot);
    Console.WriteLine(
        $"store-read sessions={core.Sessions.Sessions.Count} " +
        $"routes={core.Routes.Messages.Count} " +
        $"inbound={core.Routes.ProcessedInbound.Count} " +
        $"approvals={core.Approvals.Requests.Count} " +
        $"bindings={snapshot.Bindings.Users.Count}");

    if (args.Length == 3)
    {
        var copyPath = Path.GetFullPath(args[2]);
        if (string.Equals(sourcePath, copyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("副本目录不能是生产 Store 目录。 ");
        }
        if (Directory.Exists(copyPath) && Directory.EnumerateFileSystemEntries(copyPath).Any())
        {
            throw new InvalidOperationException("副本目录必须不存在或为空。 ");
        }
        Directory.CreateDirectory(copyPath);
        var copy = new NodeJsonStoreRepository(copyPath, NodeStoreAccess.ReadWriteCopy);
        await copy.WriteAsync(snapshot);
        var reloaded = await copy.LoadAsync();
        _ = NodeStoreCoreProjection.Project(reloaded);

        foreach (var file in NodeStoreFile.All)
        {
            var sourceFile = Path.Combine(sourcePath, file.FileName);
            if (!File.Exists(sourceFile))
            {
                continue;
            }
            var copyFile = Path.Combine(copyPath, file.FileName);
            var sourceJson = JsonNode.Parse(await File.ReadAllTextAsync(sourceFile));
            var copyJson = JsonNode.Parse(await File.ReadAllTextAsync(copyFile));
            if (!JsonNode.DeepEquals(sourceJson, copyJson))
            {
                throw new InvalidDataException($"{file.FileName} 副本回写发生语义差异。 ");
            }
        }
        Console.WriteLine($"roundtrip-copy matched={NodeStoreFile.All.Count} path={copyPath}");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"store-verify failed: {error.Message}");
    return 1;
}
