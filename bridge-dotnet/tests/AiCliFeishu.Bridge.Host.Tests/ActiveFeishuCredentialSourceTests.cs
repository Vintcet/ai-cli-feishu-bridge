using System.Net;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuCredentialSourceTests
{
    [TestMethod]
    public void LoadsProcessEnvironmentOnceWithoutReadingCredentialFile()
    {
        var fileReads = 0;
        var source = new ActiveFeishuCredentialSource(
            ActiveOptions(),
            name => name switch
            {
                "FEISHU_APP_ID" => "  cli_environment  ",
                "FEISHU_APP_SECRET" => "  environment-secret  ",
                _ => null,
            },
            _ =>
            {
                fileReads++;
                return "FEISHU_APP_ID=file";
            });

        var first = source.Credentials;
        var second = source.Credentials;

        Assert.AreSame(first, second);
        Assert.AreEqual("cli_environment", first.AppId);
        Assert.AreEqual("environment-secret", first.AppSecret);
        Assert.AreEqual(0, fileReads);
        Assert.AreEqual("Feishu credentials (redacted)", first.ToString());
        Assert.IsFalse(first.ToString().Contains(first.AppSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void UsesDotEnvForMissingValuesWithoutOverridingProcessEnvironment()
    {
        string? observedPath = null;
        var options = ActiveOptions();
        var source = new ActiveFeishuCredentialSource(
            options,
            name => name == "FEISHU_APP_ID" ? " cli_environment " : null,
            path =>
            {
                observedPath = path;
                return """
                    # production credentials
                    FEISHU_APP_ID=cli_file
                    export FEISHU_APP_SECRET="  secret#with-marker  " # comment
                    UNUSED=value
                    """;
            });

        var credentials = source.Credentials;

        Assert.AreEqual("cli_environment", credentials.AppId);
        Assert.AreEqual("secret#with-marker", credentials.AppSecret);
        Assert.AreEqual(
            Path.Combine(Path.GetDirectoryName(options.DataDirectory)!, ".env"),
            observedPath);
    }

    [TestMethod]
    public void EmptyProcessValueDoesNotFallBackToDotEnv()
    {
        var source = new ActiveFeishuCredentialSource(
            ActiveOptions(),
            name => name switch
            {
                "FEISHU_APP_ID" => "",
                "FEISHU_APP_SECRET" => null,
                _ => null,
            },
            _ => "FEISHU_APP_ID=cli_file\nFEISHU_APP_SECRET=file-secret");

        var error = Assert.ThrowsException<InvalidOperationException>(
            () => _ = source.Credentials);

        StringAssert.Contains(error.Message, "FEISHU_APP_ID");
        Assert.IsFalse(error.Message.Contains("file-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingCredentialFailureNamesOnlyConfigurationKeys()
    {
        var source = new ActiveFeishuCredentialSource(
            ActiveOptions(),
            name => name == "FEISHU_APP_ID" ? "cli_sensitive_identifier" : null,
            _ => null);

        var error = Assert.ThrowsException<InvalidOperationException>(
            () => _ = source.Credentials);

        StringAssert.Contains(error.Message, "FEISHU_APP_SECRET");
        Assert.IsFalse(error.Message.Contains(
            "cli_sensitive_identifier",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void MalformedQuotedValueFailsWithoutEchoingSecret()
    {
        const string secret = "do-not-echo-this-secret";
        var source = new ActiveFeishuCredentialSource(
            ActiveOptions(),
            _ => null,
            _ => $"FEISHU_APP_ID=cli_file\nFEISHU_APP_SECRET=\"{secret}");

        var error = Assert.ThrowsException<InvalidDataException>(
            () => _ = source.Credentials);

        StringAssert.Contains(error.Message, "未闭合");
        Assert.IsFalse(error.Message.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void RejectsPassiveOptionsBeforeReadingAnyCredentialSource()
    {
        var reads = 0;
        var source = new ActiveFeishuCredentialSource(
            BridgeHostOptions.Passive(Path.GetTempPath()),
            _ =>
            {
                reads++;
                return "value";
            },
            _ =>
            {
                reads++;
                return "value";
            });

        var error = Assert.ThrowsException<InvalidOperationException>(
            () => _ = source.Credentials);

        StringAssert.Contains(error.Message, "只能用于 Active Host");
        Assert.AreEqual(0, reads);
    }

    [TestMethod]
    public async Task LifecycleValidatesCredentialsAndReportsOnlyReadiness()
    {
        const string secret = "lifecycle-secret";
        var source = new ActiveFeishuCredentialSource(
            ActiveOptions(),
            name => name switch
            {
                "FEISHU_APP_ID" => "cli_lifecycle",
                "FEISHU_APP_SECRET" => secret,
                _ => null,
            },
            _ => null);

        Assert.AreEqual("starting", source.ComponentHealth.Status);

        await source.StartAsync(CancellationToken.None);

        Assert.AreEqual("ready", source.ComponentHealth.Status);
        Assert.AreEqual("configured", source.ComponentHealth.Detail);
        Assert.IsFalse(source.ComponentHealth.ToString()!.Contains(
            secret,
            StringComparison.Ordinal));

        await source.StopAsync(CancellationToken.None);
        Assert.AreEqual("starting", source.ComponentHealth.Status);
    }

    private static BridgeHostOptions ActiveOptions()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bridge-active-feishu-credentials-{Guid.NewGuid():N}");
        return new(
            Path.Combine(root, "data"),
            IPAddress.Loopback,
            8765,
            BridgeOwnershipMode.Active,
            "credentials-test");
    }
}
