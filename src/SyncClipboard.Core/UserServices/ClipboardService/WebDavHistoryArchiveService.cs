using SyncClipboard.Core.Commons;
using SyncClipboard.Core.Interfaces;
using SyncClipboard.Core.Models;
using SyncClipboard.Core.Models.UserConfigs;
using SyncClipboard.Core.RemoteServer.Adapter.WebDavServer;
using SyncClipboard.Core.Utilities.History;
using SyncClipboard.Core.Utilities.Web;
using SyncClipboard.Shared.Profiles;
using SyncClipboard.Shared.Profiles.Models;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SyncClipboard.Core.UserServices.ClipboardService;

/// <summary>
/// 当远程服务器为 WebDAV 时，把每一条新增的剪贴板历史记录追加归档到
/// 按天轮转的 JSON 文件中：SyncClipboard-YYYYMMDD.json，每个文件最多保留
/// <see cref="MaxRecordsPerFile"/> 条记录，超过后丢弃最旧记录。
/// 文件/图片/分组类型的记录会同时把附件上传到 file 目录并在记录中保存引用。
/// </summary>
public sealed class WebDavHistoryArchiveService : Service
{
    private const int MaxRecordsPerFile = 1000;
    private const string ArchiveFilePrefix = "SyncClipboard";
    private const string RemoteFileFolder = "file";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly ConfigManager _configManager;
    private readonly AccountManager _accountManager;
    private readonly HistoryManager _historyManager;
    private readonly IProfileEnv _profileEnv;
    private readonly IAppConfig _appConfig;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private WebDav? _webDav;
    private WebDavConfig _webDavConfig = new();
    private SyncConfig _syncConfig = new();
    private bool _archiveEnabled;
    private CancellationTokenSource? _lifetimeCts;

    /// <summary>
    /// 在事件线程上对 HistoryRecord 做同步快照，避免把 EF 跟踪实体跨线程传递。
    /// </summary>
    private sealed record ArchiveSnapshot(
        ProfileType Type,
        string Hash,
        string Text,
        string[] FilePaths,
        string? TransferDataFile,
        string? TransferDataHash,
        long Size,
        string[] Tags,
        DateTimeOffset Timestamp,
        bool IsDeleted);

    public WebDavHistoryArchiveService(
        ConfigManager configManager,
        AccountManager accountManager,
        HistoryManager historyManager,
        IProfileEnv profileEnv,
        IAppConfig appConfig,
        ILogger logger)
    {
        _configManager = configManager;
        _accountManager = accountManager;
        _historyManager = historyManager;
        _profileEnv = profileEnv;
        _appConfig = appConfig;
        _logger = logger;

        _syncConfig = configManager.GetConfig<SyncConfig>();
        configManager.ListenConfig<SyncConfig>(OnSyncConfigChanged);
    }

    protected override void StartService()
    {
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        RefreshArchiveState();
    }

    protected override async Task StopSerivceAsync()
    {
        _lifetimeCts?.Cancel();
        await _appendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeWebDav();
        }
        finally
        {
            _appendLock.Release();
        }
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }

    public override void RegistEventHandler()
    {
        _accountManager.CurrentAccountChanged += OnAccountChanged;
        _historyManager.HistoryAdded += OnHistoryAdded;
    }

    public override void UnRegistEventHandler()
    {
        _accountManager.CurrentAccountChanged -= OnAccountChanged;
        _historyManager.HistoryAdded -= OnHistoryAdded;
    }

    private void OnAccountChanged(AccountConfig accountConfig, object? config)
    {
        RefreshArchiveState();
    }

    private void OnSyncConfigChanged(SyncConfig config)
    {
        _syncConfig = config;
        // 超时、证书校验等参数变化时重建客户端使其生效。
        RefreshArchiveState();
    }

    /// <summary>
    /// 判断当前账户是否为启用了归档的 WebDAV，并据此创建/释放 WebDav 客户端。
    /// </summary>
    private void RefreshArchiveState()
    {
        var account = _configManager.GetConfig<AccountConfig>();
        var isWebDav = !account.IsEmpty() && account.AccountType == WebDavConfig.ConfigTypeName;
        var config = isWebDav
            ? _accountManager.GetConfig(account.AccountType, account.AccountId) as WebDavConfig
            : null;

        var enabled = config is not null && config.EnableHistoryArchive;

        _appendLock.Wait();
        try
        {
            if (enabled)
            {
                if (_webDav is null || !Equals(config, _webDavConfig))
                {
                    _webDavConfig = config!;
                    RecreateWebDav();
                }
            }
            else
            {
                _webDavConfig = new WebDavConfig();
                DisposeWebDav();
            }

            _archiveEnabled = enabled;
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private void RecreateWebDav()
    {
        DisposeWebDav();

        var credential = new WebDavCredential
        {
            Username = _webDavConfig.UserName,
            Password = _webDavConfig.Password,
            Url = _webDavConfig.RemoteURL,
        };

        var timeout = _syncConfig.TimeOut != 0 ? _syncConfig.TimeOut : 100u;
        var webDav = new WebDav(credential, _appConfig, _syncConfig.IgnoreCertificateErrors, _logger)
        {
            Timeout = timeout,
        };
        webDav.SetProxy(ProxyManager.CurrentProxy);
        _webDav = webDav;
    }

    private void DisposeWebDav()
    {
        _webDav?.Dispose();
        _webDav = null;
    }

    private void OnHistoryAdded(HistoryRecord record)
    {
        if (!_archiveEnabled || record.IsDeleted)
        {
            return;
        }

        var snapshot = new ArchiveSnapshot(
            record.Type,
            record.Hash,
            record.Text,
            [.. record.FilePath],
            record.TransferDataFile,
            record.TransferDataHash,
            record.Size,
            [.. record.Tags],
            new DateTimeOffset(record.Timestamp.ToUniversalTime(), TimeSpan.Zero),
            record.IsDeleted);

        var token = _lifetimeCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => AppendAsync(snapshot, token), token);
    }

    private async Task AppendAsync(ArchiveSnapshot snapshot, CancellationToken token)
    {
        try
        {
            await _appendLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!_archiveEnabled || _webDav is null)
            {
                return;
            }

            var entry = await BuildEntryAsync(snapshot, token).ConfigureAwait(false);

            var fileName = GetArchiveFileName(DateTime.Now);
            var list = await LoadTodayArchiveAsync(fileName, token).ConfigureAwait(false);
            list.Add(entry);

            // 超过上限时丢弃最旧记录，保证每个文件最多 MaxRecordsPerFile 条。
            if (list.Count > MaxRecordsPerFile)
            {
                list.RemoveRange(0, list.Count - MaxRecordsPerFile);
            }

            await SaveArchiveAsync(fileName, list, token).ConfigureAwait(false);
            _logger.Write("[WebDavArchive] Archived clipboard record", entry.Text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Write("[WebDavArchive] Failed to archive clipboard record: " + ex.Message);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private async Task<ClipboardArchiveEntry> BuildEntryAsync(ArchiveSnapshot snapshot, CancellationToken token)
    {
        var entry = new ClipboardArchiveEntry
        {
            Timestamp = snapshot.Timestamp,
            Type = snapshot.Type,
            Hash = snapshot.Hash,
            Text = snapshot.Text,
            Size = snapshot.Size,
            FilePaths = snapshot.FilePaths,
            Tags = snapshot.Tags,
        };

        if (snapshot.Type is ProfileType.File or ProfileType.Image or ProfileType.Group)
        {
            entry.DataName = snapshot.Text;
            entry.RemoteFile = await UploadAttachmentAsync(snapshot, token).ConfigureAwait(false);
        }

        return entry;
    }

    /// <summary>
    /// 把文件/图片/分组记录的附件打包上传到 file 目录，返回远端引用文件名；失败时返回 null。
    /// </summary>
    private async Task<string?> UploadAttachmentAsync(ArchiveSnapshot snapshot, CancellationToken token)
    {
        try
        {
            var profile = Profile.Create(_profileEnv.GetHistoryPersistentDir(), new ProfilePersistentInfo
            {
                Text = snapshot.Text,
                Type = snapshot.Type,
                Size = snapshot.Size,
                Hash = snapshot.Hash,
                TransferDataFile = snapshot.TransferDataFile,
                TransferDataHash = snapshot.TransferDataHash,
                FilePaths = snapshot.FilePaths,
            });

            var file = await profile.PrepareTransferData(_profileEnv.GetPersistentDir(), token).ConfigureAwait(false);
            if (file is null)
            {
                return null;
            }

            var remoteName = Profile.GetWorkingDirName(snapshot.Type, snapshot.Hash);
            await _webDav!.PutFile($"{RemoteFileFolder}/{Uri.EscapeDataString(remoteName)}", file.Path, null, token).ConfigureAwait(false);
            return remoteName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Write("[WebDavArchive] Failed to upload attachment: " + ex.Message);
            return null;
        }
    }

    private static string GetArchiveFileName(DateTime now) =>
        $"{ArchiveFilePrefix}-{now:yyyyMMdd}.json";

    private async Task<List<ClipboardArchiveEntry>> LoadTodayArchiveAsync(string fileName, CancellationToken token)
    {
        if (!await _webDav!.Exist(fileName, token).ConfigureAwait(false))
        {
            return [];
        }

        return await _webDav.GetJson<List<ClipboardArchiveEntry>>(fileName, token).ConfigureAwait(false) ?? [];
    }

    private async Task SaveArchiveAsync(string fileName, List<ClipboardArchiveEntry> list, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(list, SerializerOptions);
        await _webDav!.PutText(fileName, json, token).ConfigureAwait(false);
    }
}
