using System.Runtime.CompilerServices;
using System.Text;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral adapter from native tool-loop stream events to public chat
/// stream events, while retaining assistant text for partial persistence.
/// </summary>
public sealed class ChatStreamingResponseEngine
{
    public async IAsyncEnumerable<ChatStreamingResponseEvent> RunAsync(
        IAsyncEnumerable<ChatNativeToolStreamingLoopEvent> loopEvents,
        ChatStreamingResponseState? state = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loopEvents);

        state ??= new ChatStreamingResponseState();

        await foreach (var loopEvent in loopEvents.WithCancellation(ct))
        {
            switch (loopEvent.Kind)
            {
                case ChatNativeToolStreamingLoopEventKind.TextDelta:
                    if (loopEvent.Text is { } textDelta)
                    {
                        state.AppendAssistantContent(textDelta);
                        yield return ChatStreamingResponseEvent.Stream(
                            ChatStreamEvent.TextDelta(textDelta));
                    }

                    break;

                case ChatNativeToolStreamingLoopEventKind.BufferedText:
                    if (loopEvent.Text is { } bufferedText)
                        state.AppendAssistantContent(bufferedText);
                    break;

                case ChatNativeToolStreamingLoopEventKind.StreamEvent:
                    if (loopEvent.StreamEventValue is not null)
                    {
                        yield return ChatStreamingResponseEvent.Stream(
                            loopEvent.StreamEventValue);
                    }

                    break;

                case ChatNativeToolStreamingLoopEventKind.Completed:
                    var result = loopEvent.Result
                        ?? throw new InvalidOperationException(
                            "Core streaming loop completed without a result.");
                    state.Complete(result);
                    yield return ChatStreamingResponseEvent.Completed(result);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown native chat streaming event kind '{loopEvent.Kind}'.");
            }
        }

        if (!state.Completed)
            throw new InvalidOperationException(
                "Core streaming loop ended without a completion event.");
    }
}

public sealed class ChatStreamingResponseState
{
    private readonly StringBuilder _assistantContent = new();

    public bool Completed { get; private set; }

    public ChatNativeToolStreamingLoopResult? CompletionResult { get; private set; }

    public string PartialAssistantContent => _assistantContent.ToString();

    public int PartialAssistantContentLength => _assistantContent.Length;

    internal void AppendAssistantContent(string content) =>
        _assistantContent.Append(content);

    internal void Complete(ChatNativeToolStreamingLoopResult result)
    {
        Completed = true;
        CompletionResult = result;
    }
}

public sealed record ChatStreamingResponseEvent(
    ChatStreamingResponseEventKind Kind,
    ChatStreamEvent? StreamEvent = null,
    ChatNativeToolStreamingLoopResult? Result = null)
{
    public static ChatStreamingResponseEvent Stream(ChatStreamEvent streamEvent) =>
        new(ChatStreamingResponseEventKind.StreamEvent, StreamEvent: streamEvent);

    public static ChatStreamingResponseEvent Completed(
        ChatNativeToolStreamingLoopResult result) =>
        new(ChatStreamingResponseEventKind.Completed, Result: result);
}

public enum ChatStreamingResponseEventKind
{
    StreamEvent,
    Completed
}
