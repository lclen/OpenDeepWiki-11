using System.IO;
using System.Text;
using KoalaWiki.Core.Extensions;
using KoalaWiki.Prompts;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Newtonsoft.Json;
using OpenAI.Chat;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace KoalaWiki.KoalaWarehouse.GenerateThinkCatalogue;

public static partial class GenerateThinkCatalogueService
{
    private const int MaxRetries = 8; // 增加重试次数
    private const int BaseDelayMs = 1000;
    private const double MaxDelayMs = 30000; // 最大延迟30秒
    private const double JitterRange = 0.3; // 抖动范围30%

    // 错误分类
    private enum ErrorType
    {
        NetworkError, // 网络相关错误
        JsonParseError, // JSON解析错误
        ApiRateLimit, // API限流
        ModelError, // 模型响应错误
        UnknownError // 未知错误
    }

    public static async Task<DocumentResultCatalogue?> GenerateCatalogue(string path,
        string catalogue, Warehouse warehouse, ClassifyType? classify)
    {
        var retryCount = 0;
        Exception? lastException = null;
        var consecutiveFailures = 0;

        Log.Logger.Information("开始处理仓库：{path}，处理标题：{name}", path, warehouse.Name);

        while (retryCount < MaxRetries)
        {
            try
            {
                var result =
                    await ExecuteSingleAttempt(path, catalogue, classify, retryCount).ConfigureAwait(false);

                if (result != null)
                {
                    Log.Logger.Information("成功处理仓库：{path}，处理标题：{name}，尝试次数：{retryCount}",
                        path, warehouse.Name, retryCount + 1);
                    return result;
                }

                // result为null也算失败，继续重试
                Log.Logger.Warning("处理仓库返回空结果：{path}，处理标题：{name}，尝试次数：{retryCount}",
                    path, warehouse.Name, retryCount + 1);
                consecutiveFailures++;
            }
            catch (Exception ex)
            {
                lastException = ex;
                consecutiveFailures++;
                var errorType = ClassifyError(ex);

                Log.Logger.Warning("处理仓库失败：{path}，处理标题：{name}，尝试次数：{retryCount}，错误类型：{errorType}，错误：{error}",
                    path, warehouse.Name, retryCount + 1, errorType, ex.Message);

                // 根据错误类型决定是否继续重试
                if (!ShouldRetry(errorType, retryCount, consecutiveFailures))
                {
                    Log.Logger.Error("错误类型 {errorType} 不适合重试或达到最大重试次数，停止重试", errorType);
                    break;
                }
            }

            retryCount++;

            if (retryCount < MaxRetries)
            {
                var delay = CalculateDelay(retryCount, consecutiveFailures);
                Log.Logger.Information("等待 {delay}ms 后进行第 {nextAttempt} 次尝试", delay, retryCount + 1);
                await Task.Delay(delay);

                // 如果连续失败过多，尝试重置某些状态
                if (consecutiveFailures >= 3)
                {
                    Log.Logger.Information("连续失败 {consecutiveFailures} 次，尝试重置状态", consecutiveFailures);
                    // 可以在这里添加一些重置逻辑，比如清理缓存等
                    await Task.Delay(2000); // 额外等待
                }
            }
        }

        Log.Logger.Error("处理仓库最终失败：{path}，处理标题：{name}，总尝试次数：{totalAttempts}，最后错误：{error}",
            path, warehouse.Name, retryCount, lastException?.Message ?? "未知错误");

        return null;
    }

    private static async Task<DocumentResultCatalogue?> ExecuteSingleAttempt(
        string path, string catalogue, ClassifyType? classify, int attemptNumber)
    {
        // 根据尝试次数调整提示词策略
        var enhancedPrompt = await GenerateThinkCataloguePromptAsync(classify, catalogue);

        var history = new ChatHistory();

        history.AddSystemEnhance();

        var contents = new ChatMessageContentItemCollection()
        {
            new TextContent(enhancedPrompt),
            new TextContent(
                $"""
                 <system-reminder>
                 Return the final documentation catalogue by invoking `catalog.GenerateCatalogue` exactly once.
                 Structure requirements:
                 - Provide a JSON object with an `items` array. Each item must include `name`, `title`, and `prompt`, and may include nested `children` following the same schema.
                 - Keep reasoning concise and rely on the tool response for JSON output.
                 - `catalog.Write` and `catalog.MultiEdit` remain for compatibility but should only be used if absolutely necessary.
                 Avoid emitting JSON in chat; finish with the single tool call.
                 {Prompt.Language}
                 </system-reminder>
                 """),
            new TextContent(Prompt.Language)
        };
        history.AddUserMessage(contents);

        var catalogueTool = new CatalogueFunction();
        var analysisModel = KernelFactory.GetKernel(OpenAIOptions.Endpoint,
            OpenAIOptions.ChatApiKey, path, OpenAIOptions.AnalysisModel, false, null,
            builder => { builder.Plugins.AddFromObject(catalogueTool, "catalog"); });

        var chat = analysisModel.Services.GetService<IChatCompletionService>();
        if (chat == null)
        {
            throw new InvalidOperationException("无法获取聊天完成服务");
        }

        // 根据尝试次数调整设置
        var settings = new OpenAIPromptExecutionSettings()
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.AnalysisModel)
        };

        var inputTokenCount = 0;
        var outputTokenCount = 0;

        const int maxStreamingAttempts = 3;
        var streamingCompleted = false;
        StringBuilder? responseTranscript = null;

        for (var streamingAttempt = 0; streamingAttempt < maxStreamingAttempts && !streamingCompleted; streamingAttempt++)
        {
            responseTranscript = new StringBuilder();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

            try
            {
                await foreach (var item in chat.GetStreamingChatMessageContentsAsync(
                                   history,
                                   settings,
                                   analysisModel,
                                   cts.Token).ConfigureAwait(false))
                {
                    cts.Token.ThrowIfCancellationRequested();

                    switch (item.InnerContent)
                    {
                        case StreamingChatCompletionUpdate { Usage.InputTokenCount: > 0 } content:
                            inputTokenCount += content.Usage.InputTokenCount;
                            outputTokenCount += content.Usage.OutputTokenCount;
                            break;

                        case StreamingChatCompletionUpdate tool when tool.ToolCallUpdates.Count > 0:
                            break;

                        case StreamingChatCompletionUpdate value:
                            var text = value.ContentUpdate.FirstOrDefault()?.Text;
                            if (!string.IsNullOrEmpty(text))
                            {
                                responseTranscript.Append(text);
                                Console.Write(text);
                            }

                            break;
                    }
                }

                streamingCompleted = true;
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                if (streamingAttempt + 1 >= maxStreamingAttempts)
                {
                    throw new TimeoutException("流式处理超时");
                }

                Console.WriteLine($"超时，正在重试 ({streamingAttempt + 1}/{maxStreamingAttempts})...");
                await Task.Delay(2000, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"流式处理错误: {ex.Message}");
                throw;
            }
        }

        responseTranscript ??= new StringBuilder();

        if (!streamingCompleted)
        {
            return null;
        }

        Log.Logger.Debug("目录生成流式调用令牌统计：输入 {InputTokens}，输出 {OutputTokens}", inputTokenCount, outputTokenCount);

        DocumentResultCatalogue? parsedCatalogue = null;
        string? toolParseError = null;

        if (!string.IsNullOrWhiteSpace(catalogueTool.Content) &&
            TryDeserializeCatalogue(catalogueTool.Content, out parsedCatalogue, out toolParseError))
        {
            if (!DocumentOptions.RefineAndEnhanceQuality || attemptNumber >= 3)
            {
                return parsedCatalogue;
            }

            await RefineResponse(history, chat, settings, analysisModel);

            if (TryDeserializeCatalogue(catalogueTool.Content, out var refinedCatalogue, out var refineError))
            {
                return refinedCatalogue;
            }

            Log.Logger.Warning("细化处理后重新解析目录失败：{Error}", refineError ?? "unknown");

            return parsedCatalogue;
        }

        if (!string.IsNullOrWhiteSpace(toolParseError))
        {
            Log.Logger.Error("工具调用返回的目录 JSON 无法解析：{Error}", toolParseError);
        }

        if (parsedCatalogue == null)
        {
            var responseText = responseTranscript.ToString();
            var fallbackJson = ExtractJsonFragment(responseText);

            if (!string.IsNullOrWhiteSpace(fallbackJson) &&
                TryDeserializeCatalogue(fallbackJson, out parsedCatalogue, out var fallbackError))
            {
                return parsedCatalogue;
            }

            if (!string.IsNullOrWhiteSpace(responseText))
            {
                var preview = responseText.Length > 800 ? responseText[..800] + "…" : responseText;
                Log.Logger.Warning("降级解析模型回复失败。错误：{Error}，回复片段：{Preview}", fallbackError ?? "unknown", preview);
            }
        }

        return parsedCatalogue;
    }

    private static async Task RefineResponse(ChatHistory history, IChatCompletionService chat,
        OpenAIPromptExecutionSettings settings, Kernel kernel)
    {
        try
        {
            // 根据尝试次数调整细化策略
            const string refinementPrompt = """
                                                Refine the stored documentation_structure JSON iteratively using tools only:
                                                - Use Catalogue.Read to inspect the current JSON.
                                                - Apply several Catalogue.Edit operations to:
                                                  • add Level 2/3 subsections for core components, features, data models, integrations
                                                  • normalize kebab-case titles and maintain 'getting-started' then 'deep-dive' ordering
                                                  • enrich each section's 'prompt' with actionable guidance (scope, code areas, outputs)
                                                - Prefer localized edits; only use Catalogue.Write for a complete rewrite if necessary.
                                                - Never print JSON in chat; use tools exclusively.
                                                - Start by editing some parts that need optimization through catalog.MultiEdit.
                                            """;

            history.AddUserMessage(refinementPrompt);

            await foreach (var _ in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel))
            {
            }
        }
        catch (Exception ex)
        {
        }
    }

    private static bool TryDeserializeCatalogue(string content, out DocumentResultCatalogue? catalogue,
        out string? error)
    {
        catalogue = null;
        error = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "Content is empty.";
            return false;
        }

        try
        {
            var parsed = JsonConvert.DeserializeObject<DocumentResultCatalogue>(content);
            if (parsed == null)
            {
                error = "Deserialized catalogue is null.";
                return false;
            }

            CatalogueValidator.EnsureValid(parsed);
            catalogue = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            error = ex.Message;
            Log.Logger.Debug(ex, "解析目录 JSON 失败");
            return false;
        }
    }

    private static string? ExtractJsonFragment(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var sanitized = responseText.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var firstBrace = sanitized.IndexOf('{');
        var lastBrace = sanitized.LastIndexOf('}');

        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            return null;
        }

        return sanitized.Substring(firstBrace, lastBrace - firstBrace + 1);
    }

    private static ErrorType ClassifyError(Exception ex)
    {
        return ex switch
        {
            HttpRequestException => ErrorType.NetworkError,
            TaskCanceledException => ErrorType.NetworkError,
            JsonException => ErrorType.JsonParseError,
            InvalidOperationException when ex.Message.Contains("rate") => ErrorType.ApiRateLimit,
            InvalidOperationException when ex.Message.Contains("quota") => ErrorType.ApiRateLimit,
            _ when ex.Message.Contains("model") => ErrorType.ModelError,
            _ => ErrorType.UnknownError
        };
    }

    private static bool ShouldRetry(ErrorType errorType, int retryCount, int consecutiveFailures)
    {
        // 总是允许至少重试几次
        if (retryCount < 3) return true;

        // 根据错误类型决定是否继续重试
        return errorType switch
        {
            ErrorType.NetworkError => retryCount < MaxRetries,
            ErrorType.ApiRateLimit => retryCount < MaxRetries && consecutiveFailures < 5,
            ErrorType.JsonParseError => retryCount < 6, // JSON错误多重试几次
            ErrorType.ModelError => retryCount < 4,
            ErrorType.UnknownError => retryCount < MaxRetries,
            _ => throw new ArgumentOutOfRangeException(nameof(errorType), errorType, null)
        };
    }

    private static int CalculateDelay(int retryCount, int consecutiveFailures)
    {
        // 指数退避 + 抖动 + 连续失败惩罚
        var exponentialDelay = BaseDelayMs * Math.Pow(2, retryCount);
        var consecutiveFailurePenalty = consecutiveFailures * 1000;
        var jitter = Random.Shared.NextDouble() * JitterRange * exponentialDelay;

        var totalDelay = exponentialDelay + consecutiveFailurePenalty + jitter;

        return (int)Math.Min(totalDelay, MaxDelayMs);
    }
}
