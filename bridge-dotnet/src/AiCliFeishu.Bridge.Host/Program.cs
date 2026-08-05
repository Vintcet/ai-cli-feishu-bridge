using AiCliFeishu.Bridge.Host;

try
{
    var options = BridgeHostOptions.Parse(args, AppContext.BaseDirectory);
    await using var app = BridgeHostApplication.Build(options, args: []);
    await app.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Bridge Host 启动失败：{error.Message}");
    return 1;
}
