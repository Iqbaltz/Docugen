namespace Docugen.Core;

/// <summary>A rendered document backed by a temporary file that is deleted when the stream is disposed.</summary>
public sealed class GeneratedDocument(Stream content, string contentType, string fileName, long rowCount) : IAsyncDisposable
{
    /// <summary>Document bytes, positioned at the start.</summary>
    public Stream Content { get; } = content;

    public string ContentType { get; } = contentType;

    public string FileName { get; } = fileName;

    public long RowCount { get; } = rowCount;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
