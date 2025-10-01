namespace KoalaWiki.Dto;

public class ResponsesInput
{
    public List<ResponsesMessageInput> Messages { get; set; } = new();

    /// <summary>
    /// 组织名
    /// </summary>
    /// <returns></returns>
    public string OrganizationName { get; set; } = string.Empty;

    /// <summary>
    /// 仓库名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 多仓库选择器（可选）。当提供多个仓库时，服务将自动切换至多仓模式。
    /// </summary>
    public List<RepositorySelector>? Repositories { get; set; }

    /// <summary>
    /// 应用ID
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// 是否开启深度研究
    /// </summary>
    public bool DeepResearch { get; set; } = false;
}

public class RepositorySelector
{
    /// <summary>
    /// 仓库在系统中的唯一标识，可选。当提供时将优先于组织名和仓库名匹配。
    /// </summary>
    public string? WarehouseId { get; set; }

    /// <summary>
    /// 组织名（与旧字段保持一致以兼容旧版调用）。
    /// </summary>
    public string OrganizationName { get; set; } = string.Empty;

    /// <summary>
    /// 仓库名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 可选的别名，在多仓模式下用于展示及提示信息。
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// 目录展示前缀（例如 "repo-a:"），帮助区分不同仓库的目录结构。
    /// </summary>
    public string? Prefix { get; set; }
}

public class ResponsesMessageInput
{
    /// <summary>
    /// 消息角色：user, assistant, system
    /// </summary>
    public string Role { get; set; } = string.Empty;

    public List<ResponsesMessageContentInput>? Content { get; set; }
}

/// <summary>
/// 消息内容输入
/// </summary>
public class ResponsesMessageContentInput
{
    public string Type { get; set; } = ResponsesMessageContentType.Text;

    /// <summary>
    /// 文本内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 图片内容
    /// </summary>
    public List<ResponsesMessageContentBase64Input>? ImageContents { get; set; }

    public string? ToolId { get; set; }

    public string? TooResult { get; set; }

    public string? TooArgs { get; set; }
}

public static class ResponsesMessageContentType
{
    public const string Text = "text";

    public const string Tool = "tool";

    public const string Image = "image";

    public const string Code = "code";

    public const string Table = "table";

    public const string Link = "link";

    public const string File = "file";

    public const string Audio = "audio";

    public const string Video = "video";

    public const string Reasoning = "reasoning";

    // Text = 'text',
    // Tool = 'tool',
    // Image = 'image',
    // Code = 'code',
    // Table = 'table',
    // Link = 'link',
    // File = 'file',
    // Audio = 'audio',
    // Video = 'video',
    // Reasoning = 'reasoning',
}

public class ResponsesMessageContentBase64Input
{
    public string Data { get; set; }

    public string MimeType { get; set; }
}