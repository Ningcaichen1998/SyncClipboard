using SyncClipboard.Shared.Profiles;
using System.Text.Json.Serialization;

namespace SyncClipboard.Core.Models;

/// <summary>
/// 剪贴板历史归档中的单条记录，用于写入 WebDAV 的每日归档文件
/// （例如 SyncClipboard-20261120.json）。只保留可供日后检索与还原的关键信息。
/// </summary>
public sealed class ClipboardArchiveEntry
{
    /// <summary>该条内容首次进入剪贴板的时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProfileType Type { get; set; } = ProfileType.Text;

    public string Hash { get; set; } = string.Empty;

    /// <summary>文本内容；对文件/图片类型，存放文件名。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>文件/图片类型的原始文件名。</summary>
    public string? DataName { get; set; }

    /// <summary>附件在 WebDAV 上的引用（file 目录下的文件名）。</summary>
    public string? RemoteFile { get; set; }

    public long Size { get; set; }

    public string[] FilePaths { get; set; } = [];

    public string[] Tags { get; set; } = [];
}
