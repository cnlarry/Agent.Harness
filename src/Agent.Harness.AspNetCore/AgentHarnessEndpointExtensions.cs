using System.Text.Json;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agent.Harness.AspNetCore;

/// <summary>
/// 最小 SSE 端点：把 <see cref="AgentEvent"/> 映射为前端可消费的六种事件。
/// 认证/授权不在这里做——宿主必须在外层加 RequireAuthorization。
/// </summary>
public static class AgentHarnessEndpointExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAgentHarness(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/agent/chat")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(pattern, async (
            AgentChatRequest chatRequest,
            HttpContext context,
            IAgentTurnFactory factory,
            TurnPipeline pipeline) =>
        {
            AgentTurnRequest request;
            try
            {
                request = await factory.CreateAsync(context, chatRequest, context.RequestAborted);
            }
            catch (UnauthorizedAccessException exception)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
                return;
            }
            catch (ConversationAccessDeniedException exception)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
                return;
            }
            catch (ArgumentException exception)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";

            await foreach (var evt in pipeline.StreamAsync(request, context.RequestAborted))
            {
                await WriteEventAsync(context.Response, evt, context.RequestAborted);
            }
        });

        return endpoints;
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        AgentEvent evt,
        CancellationToken cancellationToken)
    {
        var (name, payload) = evt switch
        {
            AgentEvent.TextDelta delta => ("delta", (object)new { text = delta.Text }),
            AgentEvent.ReasoningDelta delta => ("reasoning", new { text = delta.Text }),
            AgentEvent.ToolStarted started => ("tool_start", new
            {
                callId = started.CallId,
                name = started.Name,
                risk = started.Risk.ToString(),
            }),
            AgentEvent.ToolFinished finished => ("tool_result", new
            {
                callId = finished.CallId,
                name = finished.Name,
                digest = finished.Digest,
                ok = finished.Ok,
            }),
            AgentEvent.Completed completed => ("done", completed.Result),
            AgentEvent.Failed failed => ("error", new { code = failed.Code, message = failed.Message }),
            _ => ("message", evt),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"event: {name}\ndata: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
