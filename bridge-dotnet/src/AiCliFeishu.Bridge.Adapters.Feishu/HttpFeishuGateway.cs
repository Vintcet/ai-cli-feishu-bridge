using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public sealed record FeishuGatewayOptions(
    string AppId,
    string AppSecret,
    Uri BaseUri,
    TimeSpan? TokenRefreshSkew = null)
{
    public static Uri DefaultBaseUri { get; } = new("https://open.feishu.cn/");

    public override string ToString() =>
        "Feishu Gateway options (credentials redacted)";
}

public sealed class HttpFeishuGateway : IFeishuGateway
{
    private readonly HttpClient http;
    private readonly FeishuGatewayOptions options;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private AccessToken? token;

    public HttpFeishuGateway(
        HttpClient http,
        FeishuGatewayOptions options,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.AppId) ||
            string.IsNullOrWhiteSpace(options.AppSecret) ||
            !options.BaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("飞书 Gateway 配置不完整。", nameof(options));
        }
        this.http = http;
        this.options = options;
        utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<string> SendTextAsync(
        string chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        Require(chatId, nameof(chatId));
        Require(text, nameof(text));
        var body = MessageBody(chatId, "text", JsonSerializer.Serialize(new { text }));
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Post,
                "open-apis/im/v1/messages?receive_id_type=chat_id",
                token,
                body),
            cancellationToken);
        return await ReadMessageIdAsync(response, "发送飞书文本", cancellationToken);
    }

    public async Task<string> ReplyTextAsync(
        string messageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        Require(messageId, nameof(messageId));
        Require(text, nameof(text));
        var body = new JsonObject
        {
            ["msg_type"] = "text",
            ["content"] = JsonSerializer.Serialize(new { text }),
        };
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Post,
                $"open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/reply",
                token,
                body),
            cancellationToken);
        return await ReadMessageIdAsync(response, "回复飞书文本", cancellationToken);
    }

    public async Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        Require(chatId, nameof(chatId));
        ArgumentNullException.ThrowIfNull(card);
        var body = MessageBody(chatId, "interactive", card.Content.ToJsonString());
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            body["uuid"] = idempotencyKey;
        }
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Post,
                "open-apis/im/v1/messages?receive_id_type=chat_id",
                token,
                body),
            cancellationToken);
        return await ReadMessageIdAsync(response, "发送飞书卡片", cancellationToken);
    }

    public async Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        CancellationToken cancellationToken = default)
    {
        Require(messageId, nameof(messageId));
        ArgumentNullException.ThrowIfNull(card);
        var body = new JsonObject { ["content"] = card.Content.ToJsonString() };
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Patch,
                $"open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}",
                token,
                body),
            cancellationToken);
        await EnsureFeishuSuccessAsync(response, "更新飞书卡片", cancellationToken);
    }

    public async Task<FeishuSessionGroup> CreateSessionGroupAsync(
        string ownerOpenId,
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        Require(ownerOpenId, nameof(ownerOpenId));
        Require(name, nameof(name));
        var body = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["owner_id"] = ownerOpenId,
            ["user_id_list"] = new JsonArray(ownerOpenId),
            ["group_message_type"] = "chat",
            ["chat_mode"] = "group",
            ["chat_type"] = "private",
            ["join_message_visibility"] = "only_owner",
            ["leave_message_visibility"] = "only_owner",
            ["membership_approval"] = "approval_required",
        };
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Post,
                "open-apis/im/v1/chats?user_id_type=open_id",
                token,
                body),
            cancellationToken);
        using var document = await ReadFeishuResponseAsync(
            response,
            "创建飞书会话群",
            cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            throw new HttpRequestException("创建飞书会话群成功，但响应缺少 data。 ");
        }
        return new(
            RequiredText(data, "chat_id"),
            OptionalText(data, "name")?.Trim() is { Length: > 0 } actual ? actual : name);
    }

    public async Task UpdateSessionGroupNameAsync(
        string chatId,
        string name,
        CancellationToken cancellationToken = default)
    {
        Require(chatId, nameof(chatId));
        Require(name, nameof(name));
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Put,
                $"open-apis/im/v1/chats/{Uri.EscapeDataString(chatId)}",
                token,
                new JsonObject { ["name"] = name }),
            cancellationToken);
        await EnsureFeishuSuccessAsync(response, "更新飞书会话群", cancellationToken);
    }

    public async Task DeleteSessionGroupAsync(
        string chatId,
        CancellationToken cancellationToken = default)
    {
        Require(chatId, nameof(chatId));
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Delete,
                $"open-apis/im/v1/chats/{Uri.EscapeDataString(chatId)}",
                token),
            cancellationToken);
        await EnsureFeishuSuccessAsync(response, "删除飞书会话群", cancellationToken);
    }

    public async Task<long> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        Require(messageId, nameof(messageId));
        Require(fileKey, nameof(fileKey));
        Require(destinationPath, nameof(destinationPath));
        if (resourceType is not ("image" or "file"))
        {
            throw new ArgumentOutOfRangeException(nameof(resourceType));
        }
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        using var response = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Get,
                $"open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/resources/" +
                $"{Uri.EscapeDataString(fileKey)}?type={resourceType}",
                token),
            cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"下载飞书附件失败：HTTP {(int)response.StatusCode} {body}".Trim());
        }
        if (response.Content.Headers.ContentLength is > 0 and var contentLength &&
            contentLength > maxBytes)
        {
            throw new InvalidDataException($"飞书附件超过本机限制（{maxBytes} bytes）。");
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous);
        var buffer = new byte[81_920];
        long size = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                size += read;
                if (size > maxBytes)
                {
                    throw new InvalidDataException(
                        $"飞书附件超过本机限制（{maxBytes} bytes）。");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (size == 0)
            {
                throw new InvalidDataException("飞书附件为空。 ");
            }
            return size;
        }
        catch
        {
            await destination.DisposeAsync();
            File.Delete(destinationPath);
            throw;
        }
    }

    public async Task<string> SendLocalFileAsync(
        string chatId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        Require(chatId, nameof(chatId));
        Require(filePath, nameof(filePath));
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("要发送的本地文件不存在。", filePath);
        }
        var extension = info.Extension.ToLowerInvariant();
        var image = ImageExtensions.Contains(extension) && info.Length <= 10 * 1024 * 1024;
        var field = image ? "image" : "file";
        var uploadPath = image ? "open-apis/im/v1/images" : "open-apis/im/v1/files";
        using var uploadResponse = await SendAuthorizedAsync(
            token => UploadRequest(
                token,
                uploadPath,
                filePath,
                info.Name,
                field,
                image ? "image_type" : "file_type",
                image ? "message" : FeishuFileType(extension),
                !image),
            cancellationToken);
        using var upload = await ReadFeishuResponseAsync(
            uploadResponse,
            image ? "上传飞书图片" : "上传飞书文件",
            cancellationToken);
        var uploadPayload = upload.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object
                ? data
                : upload.RootElement;
        var key = RequiredText(uploadPayload, image ? "image_key" : "file_key");
        var content = image
            ? JsonSerializer.Serialize(new { image_key = key })
            : JsonSerializer.Serialize(new { file_key = key });
        var body = MessageBody(chatId, image ? "image" : "file", content);
        using var messageResponse = await SendAuthorizedAsync(
            token => Request(
                HttpMethod.Post,
                "open-apis/im/v1/messages?receive_id_type=chat_id",
                token,
                body),
            cancellationToken);
        return await ReadMessageIdAsync(
            messageResponse,
            image ? "发送飞书图片" : "发送飞书文件",
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<string, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        var accessToken = await GetTokenAsync(cancellationToken);
        HttpResponseMessage response;
        using (var request = requestFactory(accessToken))
        {
            response = await http.SendAsync(request, completionOption, cancellationToken);
        }
        if (response.StatusCode is not HttpStatusCode.Unauthorized and
            not HttpStatusCode.Forbidden)
        {
            return response;
        }
        response.Dispose();
        InvalidateToken(accessToken);
        accessToken = await GetTokenAsync(cancellationToken);
        using var retry = requestFactory(accessToken);
        return await http.SendAsync(retry, completionOption, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var cached = token;
        if (cached is not null && cached.ExpiresAt > utcNow())
        {
            return cached.Value;
        }
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            cached = token;
            if (cached is not null && cached.ExpiresAt > utcNow())
            {
                return cached.Value;
            }
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                Resolve("open-apis/auth/v3/tenant_access_token/internal"))
            {
                Content = JsonContent.Create(new
                {
                    app_id = options.AppId,
                    app_secret = options.AppSecret,
                }),
            };
            using var response = await http.SendAsync(request, cancellationToken);
            var document = await ReadFeishuResponseAsync(
                response,
                "获取飞书 tenant access token",
                cancellationToken);
            using (document)
            {
                var root = document.RootElement;
                var value = RequiredText(root, "tenant_access_token");
                var seconds = root.TryGetProperty("expire", out var expire) &&
                    expire.TryGetInt32(out var parsed)
                    ? parsed
                    : 7_200;
                var skew = options.TokenRefreshSkew ?? TimeSpan.FromMinutes(1);
                token = new(
                    value,
                    utcNow().AddSeconds(Math.Max(1, seconds - skew.TotalSeconds)));
                return value;
            }
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private void InvalidateToken(string current)
    {
        if (token?.Value == current)
        {
            token = null;
        }
    }

    private HttpRequestMessage Request(
        HttpMethod method,
        string relativePath,
        string accessToken,
        JsonObject body)
    {
        var request = new HttpRequestMessage(method, Resolve(relativePath))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private HttpRequestMessage UploadRequest(
        string accessToken,
        string relativePath,
        string filePath,
        string fileName,
        string fileField,
        string typeField,
        string typeValue,
        bool includeFileName)
    {
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(typeValue), typeField);
            if (includeFileName)
            {
                multipart.Add(new StringContent(fileName), "file_name");
            }
            multipart.Add(new StreamContent(stream), fileField, fileName);
            return Request(HttpMethod.Post, relativePath, accessToken, multipart);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private HttpRequestMessage Request(
        HttpMethod method,
        string relativePath,
        string accessToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, Resolve(relativePath))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Uri Resolve(string relativePath) => new(options.BaseUri, relativePath);

    private static JsonObject MessageBody(string chatId, string type, string content) => new()
    {
        ["receive_id"] = chatId,
        ["msg_type"] = type,
        ["content"] = content,
    };

    private static async Task<string> ReadMessageIdAsync(
        HttpResponseMessage response,
        string context,
        CancellationToken cancellationToken)
    {
        using var document = await ReadFeishuResponseAsync(response, context, cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("data", out var data))
        {
            var direct = OptionalText(data, "message_id");
            if (direct is not null)
            {
                return direct;
            }
            if (data.TryGetProperty("message", out var message) &&
                OptionalText(message, "message_id") is { } nested)
            {
                return nested;
            }
        }
        throw new HttpRequestException($"{context}成功，但响应缺少 message_id。 ");
    }

    private static async Task EnsureFeishuSuccessAsync(
        HttpResponseMessage response,
        string context,
        CancellationToken cancellationToken)
    {
        using var _ = await ReadFeishuResponseAsync(response, context, cancellationToken);
    }

    private static async Task<JsonDocument> ReadFeishuResponseAsync(
        HttpResponseMessage response,
        string context,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{context}失败：HTTP {(int)response.StatusCode} {body}".Trim());
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException error)
        {
            throw new HttpRequestException($"{context}返回了非法 JSON。", error);
        }
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) &&
            code.TryGetInt32(out var codeValue) &&
            codeValue != 0)
        {
            var message = OptionalText(root, "msg") ?? "unknown error";
            document.Dispose();
            throw new HttpRequestException($"{context}失败：{codeValue} {message}");
        }
        return document;
    }

    private static string RequiredText(JsonElement element, string property) =>
        OptionalText(element, property) ??
        throw new HttpRequestException($"飞书响应缺少 {property}。 ");

    private static string? OptionalText(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static void Require(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("值不能为空。", parameter);
        }
    }

    private sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

    private static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(
        [".jpg", ".jpeg", ".png", ".webp", ".gif", ".tif", ".tiff", ".bmp", ".ico"],
        StringComparer.Ordinal);

    private static string FeishuFileType(string extension) => extension switch
    {
        ".opus" => "opus",
        ".mp4" => "mp4",
        ".pdf" => "pdf",
        ".doc" or ".docx" => "doc",
        ".xls" or ".xlsx" or ".csv" => "xls",
        ".ppt" or ".pptx" => "ppt",
        _ => "stream",
    };
}
