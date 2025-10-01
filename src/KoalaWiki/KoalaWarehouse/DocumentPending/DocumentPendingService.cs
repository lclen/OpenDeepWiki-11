using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace KoalaWiki.KoalaWarehouse.DocumentPending;

public partial class DocumentPendingService
{
    private static int TaskMaxSizePerUser = 3;
    private static int MinContentLength = 1000;
    private static double MinQualityScore = 60.0;
    private static double MinChineseRatio = 0.3;

    static DocumentPendingService()
    {
        // 读取环境变量
        var maxSize = Environment.GetEnvironmentVariable("TASK_MAX_SIZE_PER_USER").GetTrimmedValueOrEmpty();
        if (!string.IsNullOrEmpty(maxSize) && int.TryParse(maxSize, out var maxSizeInt))
        {
            TaskMaxSizePerUser = maxSizeInt;
        }

        // 文档质量相关配置
        var minLength = Environment.GetEnvironmentVariable("DOC_MIN_CONTENT_LENGTH").GetTrimmedValueOrEmpty();
        if (!string.IsNullOrEmpty(minLength) && int.TryParse(minLength, out var lengthInt))
        {
            MinContentLength = lengthInt;
        }

        var minScore = Environment.GetEnvironmentVariable("DOC_MIN_QUALITY_SCORE").GetTrimmedValueOrEmpty();
        if (!string.IsNullOrEmpty(minScore) && double.TryParse(minScore, out var scoreDouble))
        {
            MinQualityScore = scoreDouble;
        }
    }

    /// <summary>
    /// 处理文档生成
    /// </summary>
    /// <param name="documents"></param>
    /// <param name="fileKernel"></param>
    /// <param name="catalogue"></param>
    /// <param name="gitRepository"></param>
    /// <param name="warehouse"></param>
    /// <param name="path"></param>
    /// <param name="dbContext"></param>
    /// <param name="classifyType">
    /// 分类类型
    /// </param>
    /// <exception cref="Exception"></exception>
    public static async Task HandlePendingDocumentsAsync(List<DocumentCatalog> documents, Kernel fileKernel,
        string catalogue,
        string gitRepository, Warehouse warehouse, string path, IKoalaWikiContext dbContext, ClassifyType? classifyType)
    {
        // 提供5个并发的信号量,很容易触发429错误
        var semaphore = new SemaphoreSlim(TaskMaxSizePerUser);

        // 等待中的任务列表
        var pendingDocuments = new ConcurrentBag<DocumentCatalog>(documents);
        var runningTasks = new List<Task<(DocumentCatalog catalog, DocumentFileItem fileItem, List<string> files)>>();

        // 开始处理文档，直到所有文档都处理完成
        while (pendingDocuments.Count > 0 || runningTasks.Count > 0)
        {
            // 尝试启动新任务，直到达到并发限制
            while (pendingDocuments.Count > 0 && runningTasks.Count < TaskMaxSizePerUser)
            {
                if (!pendingDocuments.TryTake(out var documentCatalog)) continue;

                var task = ProcessDocumentAsync(documentCatalog, fileKernel, catalogue, gitRepository,
                    warehouse.Branch, path, semaphore, classifyType);
                runningTasks.Add(task);

                // 这里使用了一个小的延迟来避免过于频繁的任务启动
                await Task.Delay(1000, CancellationToken.None);
            }

            // 如果没有正在运行的任务，退出循环
            if (runningTasks.Count == 0)
                break;

            // 等待任意一个任务完成
            var completedTask = await Task.WhenAny(runningTasks);
            runningTasks.Remove(completedTask);

            try
            {
                var (catalog, fileItem, files) = await completedTask.ConfigureAwait(false);

                if (fileItem == null)
                {
                    // 构建失败
                    Log.Logger.Error("处理仓库；{path} ,处理标题：{name} 失败:文件内容为空", path, catalog.Name);
                    throw new Exception("处理失败，文件内容为空: " + catalog.Name);
                }

                // 更新文档状态
                await dbContext.DocumentCatalogs.Where(x => x.Id == catalog.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsCompleted, true));

                // 修复Mermaid语法错误
                RepairMermaid(fileItem);

                await dbContext.DocumentFileItems.AddAsync(fileItem);

                await dbContext.DocumentFileItemSources.AddRangeAsync(files.Select(x => new DocumentFileItemSource()
                {
                    Address = x,
                    DocumentFileItemId = fileItem.Id,
                    Name = Path.GetFileName(x),
                    CreatedAt = DateTime.Now,
                    Id = Guid.NewGuid().ToString("N"),
                }));

                await dbContext.SaveChangesAsync();

                Log.Logger.Information("处理仓库；{path}, 处理标题：{name} 完成并保存到数据库！", path, catalog.Name);
            }
            catch (Exception ex)
            {
                Log.Logger.Error("处理文档失败: {ex}", ex.ToString());
            }
        }
    }

    /// <summary>
    /// 处理单个文档的异步方法
    /// <returns>
    /// 返回列表
    /// </returns>
    /// </summary>

public static async Task<(DocumentCatalog catalog, DocumentFileItem fileItem, List<string> files)>
    ProcessDocumentAsync(DocumentCatalog catalog, Kernel kernel, string catalogue, string gitRepository,
        string branch,
        string path,
        SemaphoreSlim? semaphore, ClassifyType? classifyType)
{
    int retryCount = 0;
    const int retries = 5;

    while (retryCount < retries)
    {
        var files = new List<string>();

        try
        {
            if (semaphore != null)
            {
                await semaphore.WaitAsync();
            }

            Log.Logger.Information("处理仓库；{path} ,处理标题：{name}", path, catalog.Name);

            DocumentContext.DocumentStore = new DocumentStore();

            var directStopwatch = Stopwatch.StartNew();
            var directResult = await TryGenerateDocumentDirectAsync(
                catalog,
                catalogue,
                gitRepository,
                branch,
                path,
                classifyType,
                files).ConfigureAwait(false);
            directStopwatch.Stop();

            if (directResult.Success && directResult.FileItem != null)
            {
                Log.Logger.Information(
                    "文档生成（直接）完成：{name}，耗时：{elapsed}ms，质量得分：{score}",
                    catalog.Name,
                    directStopwatch.ElapsedMilliseconds,
                    directResult.Metrics?.QualityScore ?? 0);

                EnsureMermaidIntegrity(directResult.FileItem);

                return (catalog, directResult.FileItem, files);
            }

            Log.Logger.Warning(
                "文档直接生成未通过质量校验：{name}，原因：{reason}",
                catalog.Name,
                directResult.FailureReason ?? "未知");

            var fallbackStopwatch = Stopwatch.StartNew();
            var fallbackItem = await RunLegacyDocumentGenerationAsync(
                catalog,
                catalogue,
                gitRepository,
                branch,
                path,
                classifyType,
                files).ConfigureAwait(false);
            fallbackStopwatch.Stop();

            Log.Logger.Information(
                "回退路径生成完成：{name}，耗时：{elapsed}ms",
                catalog.Name,
                fallbackStopwatch.ElapsedMilliseconds);

            EnsureMermaidIntegrity(fallbackItem);

            return (catalog, fallbackItem, files);
        }
        catch (Exception ex)
        {
            retryCount++;
            Log.Logger.Error("处理仓库；{path} ,处理标题：{name} 失败:{ex}", path, catalog.Name, ex.ToString());

            if (retryCount >= retries)
            {
                Console.WriteLine($"处理 {catalog.Name} 失败，已重试 {retryCount} 次，错误：{ex.Message}");
                throw;
            }

            int delayMs = ex is InvalidOperationException && ex.Message.Contains("文档质量", StringComparison.Ordinal)
                ? 5000 * retryCount
                : 10000 * retryCount;

            Log.Logger.Information(
                "处理文档失败后重试 - 仓库: {path}, 标题: {name}, 第{retry}次重试, 等待{delay}ms",
                path,
                catalog.Name,
                retryCount,
                delayMs);

            await Task.Delay(delayMs);
        }
        finally
        {
            semaphore?.Release();
        }
    }

    throw new Exception("处理失败，重试多次仍未成功: " + catalog.Name);
}

private static async Task<GenerationAttempt> TryGenerateDocumentDirectAsync(
    DocumentCatalog catalog,
    string catalogue,
    string gitRepository,
    string branch,
    string path,
    ClassifyType? classifyType,
    List<string> files)
{
    var docs = new DocsFunction();
    var documentKernel = KernelFactory.GetKernel(
        OpenAIOptions.Endpoint,
        OpenAIOptions.ChatApiKey,
        path,
        OpenAIOptions.ChatModel,
        false,
        files,
        builder => builder.Plugins.AddFromObject(docs, "Docs"));

    var chat = documentKernel.Services.GetService<IChatCompletionService>();
    if (chat == null)
    {
        return new GenerationAttempt(false, null, null, "无法获取聊天服务");
    }

    string prompt = await GetDocumentPendingPrompt(classifyType, catalogue, gitRepository, branch, catalog.Name,
        catalog.Prompt).ConfigureAwait(false);

    var history = new ChatHistory();
    history.AddSystemDocs();

    var message = new ChatMessageContentItemCollection
    {
        new TextContent(BuildDirectInstruction(catalog, gitRepository, branch, prompt, catalogue)),
        new TextContent(Prompt.Language)
    };
    history.AddUserMessage(message);

    var settings = new OpenAIPromptExecutionSettings
    {
        ToolCallBehavior = ToolCallBehavior.RequireFunctionCall("Docs.Generate"),
        MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.ChatModel)
    };

    try
    {
        await chat.GetChatMessageContentAsync(history, settings, documentKernel).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        return new GenerationAttempt(false, null, null, $"模型调用失败: {ex.Message}");
    }

    if (string.IsNullOrWhiteSpace(docs.Content))
    {
        return new GenerationAttempt(false, null, null, "文档内容为空");
    }

    var (passed, metrics, issues) = EvaluateGeneratedContent(docs.Content);
    if (!passed)
    {
        Log.Logger.Warning("文档质量未通过：{name}，评分：{score}，问题：{issues}", catalog.Name,
            metrics.QualityScore, string.Join("; ", issues));
        return new GenerationAttempt(false, null, metrics, "文档质量校验失败");
    }

    var summary = docs.Summary;
    if (string.IsNullOrWhiteSpace(summary))
    {
        summary = await GenerateDocumentSummaryAsync(catalog, docs.Content, path).ConfigureAwait(false);
    }

    var fileItem = CreateDocumentFileItem(catalog, docs.Content, summary, metrics);

    return new GenerationAttempt(true, fileItem, metrics, null);
}

private static async Task<DocumentFileItem> RunLegacyDocumentGenerationAsync(
    DocumentCatalog catalog,
    string catalogue,
    string gitRepository,
    string branch,
    string path,
    ClassifyType? classifyType,
    List<string> files)
{
    var docs = new DocsFunction();
    var documentKernel = KernelFactory.GetKernel(
        OpenAIOptions.Endpoint,
        OpenAIOptions.ChatApiKey,
        path,
        OpenAIOptions.ChatModel,
        false,
        files,
        builder => builder.Plugins.AddFromObject(docs, "Docs"));

    var chat = documentKernel.Services.GetService<IChatCompletionService>()
               ?? throw new InvalidOperationException("无法创建聊天服务");

    string prompt = await GetDocumentPendingPrompt(classifyType, catalogue, gitRepository, branch, catalog.Name,
        catalog.Prompt).ConfigureAwait(false);

    var history = new ChatHistory();
    history.AddSystemDocs();

    var contents = new ChatMessageContentItemCollection
    {
        new TextContent(prompt)
    };
    contents.AddDocsGenerateSystemReminder();
    history.AddUserMessage(contents);

    var settings = new OpenAIPromptExecutionSettings
    {
        ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
        MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.ChatModel)
    };

    int attempt = 0;
    const int maxRetries = 3;
    int inputTokenCount = 0;
    int outputTokenCount = 0;
    CancellationTokenSource? token = null;

    while (true)
    {
        try
        {
            token?.Dispose();
            token = new CancellationTokenSource(TimeSpan.FromMinutes(30));

            await foreach (var item in chat.GetStreamingChatMessageContentsAsync(
                               history,
                               settings,
                               documentKernel,
                               token.Token).ConfigureAwait(false))
            {
                switch (item.InnerContent)
                {
                    case StreamingChatCompletionUpdate { Usage.InputTokenCount: > 0 } usage:
                        inputTokenCount += usage.Usage.InputTokenCount;
                        outputTokenCount += usage.Usage.OutputTokenCount;
                        break;
                    case StreamingChatCompletionUpdate value:
                        var text = value.ContentUpdate.FirstOrDefault()?.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            Console.Write(text);
                        }
                        break;
                }
            }

            break;
        }
        catch (OperationCanceledException) when (token?.Token.IsCancellationRequested == true)
        {
            attempt++;
            if (attempt > maxRetries)
            {
                throw new TimeoutException($"文档处理在 {maxRetries} 次重试后仍然超时");
            }

            var delayMs = Math.Min(1000 * (int)Math.Pow(2, attempt), 10000);
            await Task.Delay(delayMs, CancellationToken.None);
        }
        catch (HttpRequestException httpEx)
        {
            attempt++;
            if (attempt > maxRetries)
            {
                throw;
            }

            Log.Logger.Warning("回退路径网络错误：{name}，原因：{reason}", catalog.Name, httpEx.Message);
            await Task.Delay(3000 * attempt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            attempt++;
            if (attempt > maxRetries)
            {
                throw;
            }

            Log.Logger.Warning("回退路径未知错误：{name}，原因：{reason}", catalog.Name, ex.Message);
            await Task.Delay(5000, CancellationToken.None);
        }
    }

    token?.Dispose();

    if (string.IsNullOrWhiteSpace(docs.Content))
    {
        throw new InvalidOperationException("回退路径生成内容为空");
    }

    if (DocumentOptions.RefineAndEnhanceQuality)
    {
        try
        {
            var refineContents = new ChatMessageContentItemCollection
            {
                new TextContent(
                    """
                    Please refine and enhance the previous documentation content while maintaining its structure and approach. Focus on:

                    **Enhancement Areas:**
                    - Deepen existing architectural explanations with more technical detail
                    - Expand code analysis with additional insights from the repository
                    - Strengthen existing Mermaid diagrams with more comprehensive representations
                    - Improve clarity and readability of existing explanations
                    - Add more specific code references and examples where appropriate
                    - Enhance existing sections with additional technical depth

                    **Quality Standards:**
                    - Maintain the 90-10 description-to-code ratio established in the original
                    - Ensure all additions are evidence-based from the actual code files
                    - Preserve the Microsoft documentation style approach
                    - Enhance conceptual understanding through improved explanations
                    - Strengthen the progressive learning structure

                    **Refinement Protocol (tools only):**
                    1) Use Docs.Read to review the current document thoroughly.
                    2) Plan improvements that preserve structure and voice.
                    3) Apply multiple small, precise Docs.MultiEdit operations to improve clarity, add missing details, and strengthen diagrams/citations.
                    4) After each edit, re-run Docs.Read to verify changes and continue iterating (at least 2–3 passes).
                    5) Avoid full overwrites; prefer targeted edits that enhance existing content.

                    Build upon the solid foundation that exists to create even more comprehensive and valuable documentation.
                    """),
                new TextContent(
                    """
                    <system-reminder>
                    CRITICAL: You are now in document refinement phase. Your task is to ENHANCE and IMPROVE the EXISTING documentation content that was just generated, NOT to create completely new content.

                    MANDATORY REQUIREMENTS:
                    1. PRESERVE the original document structure and organization
                    2. ENHANCE existing explanations with more depth and clarity
                    3. IMPROVE technical accuracy and completeness based on actual code analysis
                    4. EXPAND existing sections with more detailed architectural analysis
                    5. REFINE language for better readability while maintaining technical precision
                    6. STRENGTHEN existing Mermaid diagrams or add complementary ones
                    7. ENSURE all enhancements are based on the code files analyzed in the original generation

                    FORBIDDEN ACTIONS:
                    - Do NOT restructure or reorganize the document completely
                    - Do NOT remove existing sections or content
                    - Do NOT add content not based on the analyzed code files
                    - Do NOT change the fundamental approach or style established in the original

                    Your goal is to take the good foundation that exists and make it BETTER, MORE DETAILED, and MORE COMPREHENSIVE while preserving its core structure and insights.
                    </system-reminder>
                    """),
                new TextContent(Prompt.Language)
            };
            history.AddUserMessage(refineContents);

            await chat.GetChatMessageContentAsync(history, settings, documentKernel).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(docs.Content))
            {
                Log.Logger.Warning("文档精炼后内容为空，使用原始内容，文档：{name}", catalog.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Error("文档精炼失败，使用原始内容，文档：{name}，错误：{error}", catalog.Name, ex.Message);
        }
    }

    var (passed, metrics, issues) = EvaluateGeneratedContent(docs.Content);
    if (!passed)
    {
        Log.Logger.Warning("回退路径质量检查发现问题：{name}，评分：{score}，问题：{issues}", catalog.Name,
            metrics.QualityScore, string.Join("; ", issues));
    }

    var summary = docs.Summary;
    if (string.IsNullOrWhiteSpace(summary))
    {
        summary = await GenerateDocumentSummaryAsync(catalog, docs.Content, path).ConfigureAwait(false);
    }

    return CreateDocumentFileItem(catalog, docs.Content, summary, metrics);
}

private static string BuildDirectInstruction(DocumentCatalog catalog, string gitRepository, string branch, string prompt,
    string catalogue)
{
    var builder = new StringBuilder();
    builder.AppendLine("你是一名资深的技术文档架构师，请基于提供的仓库上下文生成完整的 Markdown 文档。");
    builder.AppendLine();
    builder.AppendLine("## 仓库信息");
    builder.AppendLine($"- 仓库地址：{gitRepository}");
    builder.AppendLine($"- 分支：{branch}");
    builder.AppendLine($"- 文档标题：{catalog.Name}");
    builder.AppendLine();
    builder.AppendLine("## 目录结构（摘录）");
    builder.AppendLine(catalogue);
    builder.AppendLine();
    builder.AppendLine("## 任务说明");
    builder.AppendLine("- 仔细阅读 `代码上下文` 中的提示，理解系统架构、功能模块和业务背景。");
    builder.AppendLine("- 直接调用 `Docs.Generate`，一次性返回完整的 Markdown 文档字符串，不要拆分多次输出。");
    builder.AppendLine($"- 正文长度不少于 {MinContentLength} 个字符，至少包含 5 个章节标题和 3 个 Mermaid 图。");
    builder.AppendLine("- 提供安装/部署、架构分析、核心模块解析、运行流程、API/接口说明、最佳实践与常见问题。");
    builder.AppendLine("- 使用简体中文撰写，可在需要时补充英文术语，并为关键代码或文件提供 Markdown 链接引用。");
    builder.AppendLine("- 在文末提供参考资料或仓库链接汇总。");
    builder.AppendLine("- 如果判断资料不足以完成任务，请明确说明原因。");
    builder.AppendLine();
    builder.AppendLine("## 代码上下文");
    builder.AppendLine(prompt);
    builder.AppendLine();
    builder.AppendLine("完成后，如生成了精简摘要，可调用 `Docs.Summarize` 返回不超过 200 字的概要。");

    return builder.ToString();
}

private static async Task<string?> GenerateDocumentSummaryAsync(DocumentCatalog catalog, string content, string path)
{
    try
    {
        var docs = new DocsFunction();
        var kernel = KernelFactory.GetKernel(
            OpenAIOptions.Endpoint,
            OpenAIOptions.ChatApiKey,
            path,
            OpenAIOptions.ChatModel,
            false,
            new List<string>(),
            builder => builder.Plugins.AddFromObject(docs, "Docs"));

        var chat = kernel.Services.GetService<IChatCompletionService>();
        if (chat == null)
        {
            return null;
        }

        var history = new ChatHistory();
        history.AddSystemMessage("你负责为技术文档生成摘要。");

        var summarySource = content.Length > 4000 ? content[..4000] : content;
        var builder = new StringBuilder();
        builder.AppendLine("请基于以下 Markdown 文档生成一段不超过 200 字的中文摘要，聚焦关键功能、技术栈与架构亮点：");
        builder.AppendLine("```markdown");
        builder.AppendLine(summarySource);
        builder.AppendLine("```");

        var message = new ChatMessageContentItemCollection
        {
            new TextContent(builder.ToString()),
            new TextContent(Prompt.Language)
        };
        history.AddUserMessage(message);

        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.RequireFunctionCall("Docs.Summarize"),
            MaxTokens = 512
        };

        await chat.GetChatMessageContentAsync(history, settings, kernel).ConfigureAwait(false);

        return docs.Summary;
    }
    catch (Exception ex)
    {
        Log.Logger.Warning("生成摘要失败：{name}，原因：{reason}", catalog.Name, ex.Message);
        return null;
    }
}

private static DocumentFileItem CreateDocumentFileItem(DocumentCatalog catalog, string content, string? summary,
    DocumentQualityMetrics? metrics)
{
    var fileItem = new DocumentFileItem
    {
        Content = content,
        DocumentCatalogId = catalog.Id,
        Description = string.Empty,
        Extra = new Dictionary<string, string>(),
        Metadata = new Dictionary<string, string>(),
        Source = [],
        CommentCount = 0,
        RequestToken = 0,
        CreatedAt = DateTime.Now,
        Id = Guid.NewGuid().ToString("N"),
        ResponseToken = 0,
        Size = 0,
        Title = catalog.Name
    };

    if (!string.IsNullOrWhiteSpace(summary))
    {
        fileItem.Metadata["summary"] = summary;
    }

    if (metrics != null)
    {
        fileItem.Metadata["quality_score"] = metrics.QualityScore.ToString("F2");
        fileItem.Metadata["content_length"] = metrics.ContentLength.ToString();
        fileItem.Metadata["mermaid_count"] = metrics.MermaidDiagramCount.ToString();
        fileItem.Metadata["heading_count"] = metrics.HeadingCount.ToString();
    }

    return fileItem;
}

private static void EnsureMermaidIntegrity(DocumentFileItem fileItem)
{
    var (isValid, issues) = ValidateMermaidSyntax(fileItem.Content);
    if (isValid)
    {
        return;
    }

    Log.Logger.Warning("Mermaid 校验发现问题：{issues}", string.Join("; ", issues));
    RepairMermaid(fileItem);

    var (revalidated, remaining) = ValidateMermaidSyntax(fileItem.Content);
    if (!revalidated)
    {
        Log.Logger.Error("Mermaid 修复后仍存在问题：{issues}", string.Join("; ", remaining));
    }
}

internal static (bool Passed, DocumentQualityMetrics Metrics, List<string> Issues) EvaluateGeneratedContent(string? content)
{
    var metrics = new DocumentQualityMetrics();
    var issues = new List<string>();

    if (string.IsNullOrWhiteSpace(content))
    {
        metrics.ContentLength = 0;
        metrics.QualityScore = 0;
        issues.Add("文档内容为空");
        return (false, metrics, issues);
    }

    var normalized = content.Trim();
    metrics.ContentLength = normalized.Length;
    metrics.HeadingCount = Regex.Matches(normalized, @"(?m)^#{1,6}\s+").Count;
    metrics.MermaidDiagramCount = Regex.Matches(normalized, @"```mermaid").Count;
    metrics.CodeBlockCount = Regex.Matches(normalized, @"```(?!mermaid)[\s\S]*?```").Count;
    metrics.LinkCount = Regex.Matches(normalized, @"\[[^\]]+\]\([^\)]+\)").Count;
    metrics.ChineseCharacterRatio = CalculateChineseRatio(normalized);

    if (metrics.ContentLength < MinContentLength)
    {
        issues.Add($"文档长度不足：{metrics.ContentLength}/{MinContentLength}");
    }

    if (metrics.ChineseCharacterRatio < MinChineseRatio)
    {
        issues.Add($"中文占比过低：{metrics.ChineseCharacterRatio:P0}");
    }

    if (metrics.HeadingCount < 5)
    {
        issues.Add("章节数量不足（至少需要 5 个标题）");
    }

    if (metrics.MermaidDiagramCount < 3)
    {
        issues.Add("Mermaid 图数量不足（至少需要 3 个）");
    }

    if (metrics.LinkCount == 0)
    {
        issues.Add("缺少引用或链接");
    }

    metrics.QualityScore = CalculateQualityScore(metrics, issues.Count);

    if (metrics.QualityScore < MinQualityScore)
    {
        issues.Add($"质量评分低于阈值 {MinQualityScore:0.##}");
    }

    var passed = issues.Count == 0;
    return (passed, metrics, issues);
}

private static double CalculateChineseRatio(string content)
{
    if (string.IsNullOrEmpty(content))
    {
        return 0;
    }

    int chineseCount = content.Count(c => c >= '一' && c <= '鿿');
    return content.Length == 0 ? 0 : (double)chineseCount / content.Length;
}

internal static (bool IsValid, List<string> Issues) ValidateMermaidSyntax(string? content)
{
    var issues = new List<string>();

    if (string.IsNullOrWhiteSpace(content))
    {
        issues.Add("文档为空，无法校验 Mermaid");
        return (false, issues);
    }

    var regex = new Regex("```mermaid\s*([\s\S]*?)```", RegexOptions.Multiline);
    var matches = regex.Matches(content);

    if (matches.Count == 0)
    {
        issues.Add("未检测到 Mermaid 图表");
        return (false, issues);
    }

    foreach (Match match in matches)
    {
        var diagram = match.Groups[1].Value;
        if (!Regex.IsMatch(diagram, @"\b(graph|sequenceDiagram|classDiagram|stateDiagram|erDiagram|journey|gantt|mindmap|timeline)\b"))
        {
            issues.Add("存在无法识别的 Mermaid 图表类型");
        }

        if (diagram.Count(c => c == '(') != diagram.Count(c => c == ')'))
        {
            issues.Add("Mermaid 图括号数量不匹配");
        }
    }

    return (issues.Count == 0, issues);
}

private sealed record GenerationAttempt(
    bool Success,
    DocumentFileItem? FileItem,
    DocumentQualityMetrics? Metrics,
    string? FailureReason);
    /// <summary>
    /// 计算文档质量评分
    /// </summary>
    private static double CalculateQualityScore(DocumentQualityMetrics metrics, int issueCount)
    {
        double score = 100.0;

        if (metrics.ContentLength < MinContentLength) score -= 30;
        else if (metrics.ContentLength < MinContentLength * 1.2) score -= 10;

        if (metrics.HeadingCount < 5) score -= 10;
        if (metrics.MermaidDiagramCount < 3) score -= 10;
        if (metrics.CodeBlockCount < 2) score -= 5;
        if (metrics.LinkCount == 0) score -= 5;
        if (metrics.ChineseCharacterRatio < MinChineseRatio) score -= 10;

        score -= issueCount * 5;

        return Math.Max(score, 0);
    }

    /// <summary>
    /// 文档质量指标
    /// </summary>
    public class DocumentQualityMetrics
    {
        public int ContentLength { get; set; }
        public double QualityScore { get; set; }
        public double ChineseCharacterRatio { get; set; }
        public int MermaidDiagramCount { get; set; }
        public int HeadingCount { get; set; }
        public int CodeBlockCount { get; set; }
        public int LinkCount { get; set; }
    }

    /// <summary>
    /// Mermaid可能存在语法错误，使用大模型进行修复
    /// </summary>
    /// <param name="fileItem"></param>
    public static void RepairMermaid(DocumentFileItem fileItem)
    {
        try
        {
            var regex = new Regex(@"```mermaid\s*([\s\S]*?)```", RegexOptions.Multiline);
            var matches = regex.Matches(fileItem.Content);

            foreach (Match match in matches)
            {
                var code = match.Groups[1].Value;

                // 只需要删除[]里面的(和)，它可能单独处理
                var codeWithoutBrackets =
                    Regex.Replace(code, @"\[[^\]]*\]",
                        m => m.Value.Replace("(", "").Replace(")", "").Replace("（", "").Replace("）", ""));
                // 然后替换原有内容
                fileItem.Content = fileItem.Content.Replace(match.Value, $"```mermaid\n{codeWithoutBrackets}```");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "修复mermaid语法失败");
        }
    }
}
