using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FastService;
using KoalaWiki.Dto;
using KoalaWiki.Functions;
using KoalaWiki.Prompts;
using KoalaWiki.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

#pragma warning disable SKEXP0001

namespace KoalaWiki.Services.AI;

[Tags("Responese")]
[FastService.Route("")]
public class ResponsesService(IKoalaWikiContext koala) : FastApi
{
    [HttpPost("/api/Responses")]
    public async Task ExecuteAsync(HttpContext context, ResponsesInput input)
    {
        using var activity = Activity.Current?.Source.StartActivity("AI.ResponsesService.Execute");
        activity?.SetTag("repository.organization", input.OrganizationName);
        activity?.SetTag("repository.name", input.Name);
        activity?.SetTag("message.count", input.Messages?.Count ?? 0);
        activity?.SetTag("model.provider", OpenAIOptions.ModelProvider);
        activity?.SetTag("model.name", OpenAIOptions.ChatModel);

        static string DescribeSelector(RepositorySelector selector)
        {
            if (!string.IsNullOrWhiteSpace(selector.WarehouseId))
            {
                return selector.WarehouseId!;
            }

            var organization = string.IsNullOrWhiteSpace(selector.OrganizationName)
                ? ""
                : selector.OrganizationName;
            var name = string.IsNullOrWhiteSpace(selector.Name) ? "" : selector.Name;

            return string.IsNullOrWhiteSpace(organization)
                ? name
                : $"{organization}/{name}";
        }

        static string ResolveLabel(Warehouse warehouse, RepositorySelector? selector)
        {
            if (!string.IsNullOrWhiteSpace(selector?.Alias))
            {
                return selector!.Alias!;
            }

            return string.IsNullOrWhiteSpace(warehouse.OrganizationName)
                ? warehouse.Name
                : $"{warehouse.OrganizationName}/{warehouse.Name}";
        }

        static string ApplyPrefix(string text, string prefix)
        {
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var builder = new StringBuilder();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    builder.AppendLine(line);
                }
                else
                {
                    builder.AppendLine($"{prefix}{line}");
                }
            }

            return builder.ToString().TrimEnd();
        }

        static string BuildCatalogue(IReadOnlyList<RepositoryContext> contexts)
        {
            if (contexts.Count == 1 && string.IsNullOrWhiteSpace(contexts[0].Selector?.Prefix))
            {
                return contexts[0].Catalogue;
            }

            var builder = new StringBuilder();

            for (var i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i];
                var label = ResolveLabel(ctx.Warehouse, ctx.Selector);
                var prefix = string.IsNullOrWhiteSpace(ctx.Selector?.Prefix)
                    ? $"[{label}] "
                    : ctx.Selector!.Prefix!;

                builder.AppendLine($"## Repository {i + 1}: {label}");
                builder.AppendLine($"Source: {ctx.Warehouse.Address.Replace(".git", string.Empty)}");
                builder.AppendLine($"Branch: {ctx.Warehouse.Branch}");
                builder.AppendLine();
                builder.AppendLine(ApplyPrefix(ctx.Catalogue, prefix));

                if (i < contexts.Count - 1)
                {
                    builder.AppendLine();
                }
            }

            return builder.ToString().Trim();
        }

        // URL decode parameters for backwards compatibility
        var decodedOrganizationName = HttpUtility.UrlDecode(input.OrganizationName);
        var decodedName = HttpUtility.UrlDecode(input.Name);

        record RepositoryContext(Warehouse Warehouse, Document Document, RepositorySelector? Selector, string Catalogue);

        var repositoryContexts = new List<RepositoryContext>();
        var missingRepositories = new List<string>();

        if (input.Repositories is { Count: > 0 })
        {
            foreach (var selector in input.Repositories)
            {
                if (selector == null)
                {
                    continue;
                }

                Warehouse? targetWarehouse;

                if (!string.IsNullOrWhiteSpace(selector.WarehouseId))
                {
                    targetWarehouse = await koala.Warehouses
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id.ToLower() == selector.WarehouseId!.ToLower());
                }
                else
                {
                    var selectorOrganization = HttpUtility.UrlDecode(selector.OrganizationName ?? string.Empty);
                    var selectorName = HttpUtility.UrlDecode(selector.Name ?? string.Empty);

                    targetWarehouse = await koala.Warehouses
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x =>
                            x.OrganizationName.ToLower() == selectorOrganization.ToLower() &&
                            x.Name.ToLower() == selectorName.ToLower());
                }

                if (targetWarehouse == null)
                {
                    missingRepositories.Add(DescribeSelector(selector));
                    continue;
                }

                var targetDocument = await koala.Documents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WarehouseId == targetWarehouse.Id);

                if (targetDocument == null)
                {
                    missingRepositories.Add($"{DescribeSelector(selector)}:document");
                    continue;
                }

                var catalogue = targetDocument.GetCatalogueSmartFilterOptimized();
                repositoryContexts.Add(new RepositoryContext(targetWarehouse, targetDocument, selector, catalogue));
            }
        }
        else
        {
            var warehouse = await koala.Warehouses
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.OrganizationName.ToLower() == decodedOrganizationName.ToLower() &&
                    x.Name.ToLower() == decodedName.ToLower());

            if (warehouse == null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Warehouse not found");
                activity?.SetTag("error.reason", "warehouse_not_found");
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Warehouse not found",
                    code = 404,
                });
                return;
            }

            var document = await koala.Documents
                .AsNoTracking()
                .Where(x => x.WarehouseId == warehouse.Id)
                .FirstOrDefaultAsync();

            if (document == null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Document not found");
                activity?.SetTag("error.reason", "document_not_found");
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "document not found",
                    code = 404,
                });
                return;
            }

            repositoryContexts.Add(new RepositoryContext(warehouse, document, null, document.GetCatalogueSmartFilterOptimized()));
        }

        if (repositoryContexts.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Warehouse not found");
            activity?.SetTag("error.reason", "warehouse_not_found");
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Warehouse not found",
                missing = missingRepositories,
                code = 404,
            });
            return;
        }

        if (missingRepositories.Count > 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Partial warehouse resolution failure");
            activity?.SetTag("error.reason", "partial_warehouse_not_found");
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "One or more warehouses could not be resolved",
                missing = missingRepositories,
                code = 404,
            });
            return;
        }

        var isMultiRepository = repositoryContexts.Count > 1;
        var primaryContext = repositoryContexts[0];

        activity?.SetTag("warehouse.count", repositoryContexts.Count);
        activity?.SetTag("warehouse.primary_id", primaryContext.Warehouse.Id);
        activity?.SetTag("warehouse.address", primaryContext.Warehouse.Address);
        activity?.SetTag("warehouse.branch", primaryContext.Warehouse.Branch);
        activity?.SetTag("document.id", primaryContext.Document.Id);
        activity?.SetTag("document.git_path", primaryContext.Document.GitPath);

        if (isMultiRepository)
        {
            activity?.SetTag("warehouse.ids", string.Join(',', repositoryContexts.Select(x => x.Warehouse.Id)));
        }

        // 解析仓库的目录结构
        var path = primaryContext.Document.GitPath;

        using var kernelCreateActivity = Activity.Current.Source.StartActivity("AI.KernelCreation");
        kernelCreateActivity?.SetTag("kernel.path", path);
        kernelCreateActivity?.SetTag("kernel.model", OpenAIOptions.ChatModel);

        var kernel = KernelFactory.GetKernel(OpenAIOptions.Endpoint,
            OpenAIOptions.ChatApiKey, path, OpenAIOptions.ChatModel, false);

        kernelCreateActivity?.SetStatus(ActivityStatusCode.Ok);

        if (OpenAIOptions.EnableMem0)
        {
            var warehouseIds = repositoryContexts.Select(x => x.Warehouse.Id).Distinct().ToList();
            kernel.Plugins.AddFromObject(new RagTool(warehouseIds));
        }

        for (var index = 0; index < repositoryContexts.Count; index++)
        {
            var repositoryContext = repositoryContexts[index];
            var pluginLabel = ResolveLabel(repositoryContext.Warehouse, repositoryContext.Selector)
                .Replace(' ', '_');

            if (repositoryContext.Warehouse.Address.Contains("github.com"))
            {
                kernel.Plugins.AddFromObject(new GithubTool(repositoryContext.Warehouse.OrganizationName,
                        repositoryContext.Warehouse.Name,
                        repositoryContext.Warehouse.Branch),
                    repositoryContexts.Count == 1 ? "Github" : $"Github_{index + 1}_{pluginLabel}");
            }
            else if (repositoryContext.Warehouse.Address.Contains("gitee.com") &&
                     !string.IsNullOrWhiteSpace(GiteeOptions.Token))
            {
                kernel.Plugins.AddFromObject(new GiteeTool(repositoryContext.Warehouse.OrganizationName,
                        repositoryContext.Warehouse.Name,
                        repositoryContext.Warehouse.Branch),
                    repositoryContexts.Count == 1 ? "Gitee" : $"Gitee_{index + 1}_{pluginLabel}");
            }
        }

        DocumentContext.DocumentStore = new DocumentStore();


        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();

        string tree = BuildCatalogue(repositoryContexts);

        var repositoryNames = repositoryContexts.Select(x => ResolveLabel(x.Warehouse, x.Selector)).ToList();
        var repositoryAddresses = repositoryContexts
            .Select(x => x.Warehouse.Address.Replace(".git", string.Empty)).ToList();
        var branchDescriptions = repositoryContexts
            .Select(x => $"{ResolveLabel(x.Warehouse, x.Selector)}:{x.Warehouse.Branch}").ToList();

        if (input.DeepResearch)
        {
            history.AddSystemMessage(await PromptContext.Chat(nameof(PromptConstant.Chat.ResponsesDeepResearch),
                new KernelArguments()
                {
                    ["catalogue"] = tree,
                    ["repository"] = string.Join('\n', repositoryAddresses),
                    ["repository_name"] = string.Join(", ", repositoryNames),
                    ["branch"] = string.Join(", ", branchDescriptions),
                    ["repository_count"] = repositoryContexts.Count
                }, OpenAIOptions.DeepResearchModel));
        }
        else
        {
            history.AddSystemMessage(await PromptContext.Chat(nameof(PromptConstant.Chat.Responses),
                new KernelArguments()
                {
                    ["catalogue"] = tree,
                    ["repository"] = string.Join('\n', repositoryAddresses),
                    ["repository_name"] = string.Join(", ", repositoryNames),
                    ["branch"] = string.Join(", ", branchDescriptions),
                    ["repository_count"] = repositoryContexts.Count
                }, OpenAIOptions.DeepResearchModel));
        }

        if (!string.IsNullOrEmpty(input.AppId))
        {
            var appConfig = await koala.AppConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AppId == input.AppId);

            if (appConfig == null)
            {
                throw new Exception(
                    "AppConfig is not supported in this endpoint. Please use the appropriate API for app configurations.");
            }


            if (!string.IsNullOrEmpty(appConfig?.Prompt))
            {
                history.AddUserMessage($"<system>\n{appConfig?.Prompt}\n</system>");
            }
        }

        // 添加消息历史记录
        foreach (var msg in input.Messages)
        {
            if (msg.Role.ToLower() == "user")
            {
                if (msg.Content?.Count == 1 && msg.Content.Any(x => x.Type == ResponsesMessageContentType.Text))
                {
                    history.AddUserMessage(msg.Content.First().Content);
                    continue;
                }

                var contents = new ChatMessageContentItemCollection();

                foreach (var messageContentInput in msg.Content)
                {
                    if (messageContentInput.Type == ResponsesMessageContentType.Text)
                    {
                        contents.Add(new TextContent(messageContentInput.Content));
                    }
                    else if (messageContentInput.Type == ResponsesMessageContentType.Image &&
                             messageContentInput.ImageContents != null)
                    {
                        foreach (var imageContent in messageContentInput.ImageContents)
                        {
                            contents.Add(new BinaryContent(
                                $"data:{imageContent.MimeType};base64,{imageContent.Data}"));
                        }
                    }
                    else
                    {
                    }
                }
            }
            else if (msg.Role.ToLower() == "assistant")
            {
                // 判断，如果当前消息是最后一条，并且content=空则跳过
                if (msg.Content == null ||
                    msg.Content.Count > 0 && msg.Content.All(x => string.IsNullOrEmpty(x.Content)))
                {
                    continue;
                }

                if (msg.Content?.Count == 1 && msg.Content.Any(x => x.Type == ResponsesMessageContentType.Text))
                {
                    history.AddUserMessage(msg.Content.First().Content);
                    continue;
                }

                var contents = new ChatMessageContentItemCollection();
                foreach (var messageContentInput in msg.Content)
                {
                    if (messageContentInput.Type == ResponsesMessageContentType.Text)
                    {
                        contents.Add(new TextContent(messageContentInput.Content));
                    }
                    else if (messageContentInput.Type == ResponsesMessageContentType.Image)
                    {
                        // 图片内容
                        var imageContent = new ImageContent(messageContentInput.Content);
                        contents.Add(new BinaryContent(
                            $"data:{imageContent.MimeType};base64,{imageContent.Data}"));
                    }
                    else
                    {
                    }
                }
            }
            else if (msg.Role.ToLower() == "system")
            {
                if (msg.Content?.Count == 1 && msg.Content.Any(x => x.Type == ResponsesMessageContentType.Text))
                {
                    history.AddUserMessage(msg.Content.First().Content);
                    continue;
                }
            }
        }

        // sse
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // 是否推理中
        var isReasoning = false;

        // 是否普通消息
        var isMessage = false;

        using var chatActivity = Activity.Current?.Source.StartActivity("AI.StreamingChatCompletion");
        chatActivity?.SetTag("chat.max_tokens", DocumentsHelper.GetMaxTokens(OpenAIOptions.DeepResearchModel));
        chatActivity?.SetTag("chat.temperature", 0.5);
        chatActivity?.SetTag("chat.tool_behavior", "AutoInvokeKernelFunctions");
        chatActivity?.SetTag("chat.history_count", history.Count);

        var messageCount = 0;
        var toolCallCount = 0;
        var reasoningTokens = 0;

        await foreach (var chatItem in chat.GetStreamingChatMessageContentsAsync(history,
                           new OpenAIPromptExecutionSettings()
                           {
                               ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                               MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.DeepResearchModel),
                               Temperature = 0.5,
                           }, kernel))
        {
            // 发送数据
            if (chatItem.InnerContent is not StreamingChatCompletionUpdate message) continue;

            if (DocumentContext.DocumentStore != null && DocumentContext.DocumentStore.GitIssus.Count > 0)
            {
                await context.Response.WriteAsync(
                    $"data: {{\"type\": \"git_issues\", \"content\": {JsonSerializer.Serialize(DocumentContext.DocumentStore.GitIssus, JsonSerializerOptions.Web)}}}\n\n");

                await context.Response.Body.FlushAsync();

                DocumentContext.DocumentStore.GitIssus.Clear();
            }


            var jsonContent = JsonNode.Parse(ModelReaderWriter.Write(chatItem.InnerContent!));

            var choices = jsonContent!["choices"] as JsonArray;
            if (choices?.Count > 0)
            {
                if (choices[0]!["delta"]!["reasoning_content"] != null)
                {
                    // 推理内容
                    var reasoningContent = choices![0]!["delta"]!["reasoning_content"].ToString();
                    if (!string.IsNullOrEmpty(reasoningContent))
                    {
                        if (isReasoning == false)
                        {
                            // 结束普通消息
                            if (isMessage)
                            {
                                isMessage = false;

                                await context.Response.WriteAsync($"data: {{\"type\": \"message_end\"}}\n\n");
                            }

                            isReasoning = true;

                            // 发送开启推理事件
                            await context.Response.WriteAsync($"data: {{\"type\": \"reasoning_start\"}}\n\n");
                        }

                        await context.Response.WriteAsync(
                            $"data: {{\"type\": \"reasoning_content\", \"content\": {JsonSerializer.Serialize(reasoningContent)}}}\n\n");
                        await context.Response.Body.FlushAsync();
                        reasoningTokens += reasoningContent.Length / 4; // 粗略估算令牌数
                        continue;
                    }
                }
            }

            if (isReasoning)
            {
                // 结束推理
                isReasoning = false;
                await context.Response.WriteAsync($"data: {{\"type\": \"reasoning_end\"}}\n\n");

                isMessage = true;
                // 开启普通消息
                await context.Response.WriteAsync($"data: {{\"type\": \"message_start\"}}\n\n");
            }

            if (message.ToolCallUpdates.Count > 0)
            {
                // 工具调用更新
                foreach (var toolCallUpdate in message.ToolCallUpdates)
                {
                    var toolCallData = new
                    {
                        type = "tool_call",
                        tool_call_id = toolCallUpdate.ToolCallId,
                        function_name = toolCallUpdate.FunctionName,
                        function_arguments = Encoding.UTF8.GetString(toolCallUpdate.FunctionArgumentsUpdate),
                    };

                    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(toolCallData)}\n\n");
                    await context.Response.Body.FlushAsync();
                }

                toolCallCount++;
                continue;
            }

            // 普通消息内容
            if (!string.IsNullOrEmpty(chatItem.Content))
            {
                if (!isMessage)
                {
                    isMessage = true;
                    // 开启普通消息
                    await context.Response.WriteAsync($"data: {{\"type\": \"message_start\"}}\n\n");
                }

                var messageData = new
                {
                    type = "message_content",
                    content = chatItem.Content
                };

                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(messageData)}\n\n");
                await context.Response.Body.FlushAsync();
                messageCount++;
            }
        }

        // 确保最后结束消息
        if (isMessage)
        {
            await context.Response.WriteAsync($"data: {{\"type\": \"message_end\"}}\n\n");
        }

        if (isReasoning)
        {
            await context.Response.WriteAsync($"data: {{\"type\": \"reasoning_end\"}}\n\n");
        }

        // 发送结束事件
        await context.Response.WriteAsync($"data: {{\"type\": \"done\"}}\n\n");
        await context.Response.Body.FlushAsync();

        // 设置聊天活动的统计信息
        chatActivity?.SetTag("chat.message_count", messageCount);
        chatActivity?.SetTag("chat.tool_call_count", toolCallCount);
        chatActivity?.SetTag("chat.reasoning_tokens", reasoningTokens);
        chatActivity?.SetStatus(ActivityStatusCode.Ok);

        // 设置主活动状态
        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("response.completed", true);
    }
}