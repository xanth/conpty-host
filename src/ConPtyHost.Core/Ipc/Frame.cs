using System.Text.Json.Serialization;

namespace ConPtyHost.Core.Ipc;

/// <summary>One message between a tmux client process and the server.</summary>
/// <remarks>
/// Client to server: <c>command</c> (run <see cref="Commands"/>), <c>attach</c>
/// (become a control client), <c>line</c> (one control-mode command).
/// Server to client: <c>out</c> (bytes for stdout), <c>err</c> (a message for
/// stderr), <c>exit</c> (the exit code; the connection then closes).
/// </remarks>
public sealed record Frame
{
    public required string Kind { get; init; }

    public List<List<string>>? Commands { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Raw bytes: stdin for <c>command</c>, stdout for <c>out</c>.</summary>
    public byte[]? Data { get; init; }

    public string? Text { get; init; }

    public int Code { get; init; }
}

[JsonSerializable(typeof(Frame))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class FrameJsonContext : JsonSerializerContext;
