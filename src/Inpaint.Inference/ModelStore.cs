namespace Inpaint.Inference;

/// <summary>
/// 模型文件的本地缓存（应用数据目录）。首次使用时流式下载并上报进度，
/// 与网页版 ensureModel 语义一致；写入临时文件后原子替换，避免半成品被当成缓存。
/// </summary>
public static class ModelStore
{
    public static string ModelsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inpaint", "models");

    public static string GetPath(ModelInfo model) => Path.Combine(ModelsDir, model.FileName);

    public static bool Exists(ModelInfo model) => File.Exists(GetPath(model));

    public static async Task EnsureAsync(
        ModelInfo model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Exists(model)) return;
        Directory.CreateDirectory(ModelsDir);
        try
        {
            await DownloadAsync(model.Url, GetPath(model), progress, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException && model.BackupUrl is not null)
        {
            await DownloadAsync(model.BackupUrl, GetPath(model), progress, ct);
        }
    }

    private static async Task DownloadAsync(
        string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        string tempPath = destPath + ".downloading";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0) progress?.Report(downloaded * 100.0 / total);
            }
        }
        File.Move(tempPath, destPath, overwrite: true);
    }
}
