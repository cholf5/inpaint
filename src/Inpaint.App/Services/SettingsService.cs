using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inpaint.App.Services;

/// <summary>
/// settings.json 读写（与模型缓存同在应用数据目录/Inpaint 下）：枚举存名字便于手改，缩进输出；
/// 写入临时文件后原子替换。文件缺失或损坏时回退默认值；数值越界收敛到合法区间。
/// path 参数供单测注入临时路径。
/// </summary>
public static class SettingsService
{
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inpaint", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                           ?? new AppSettings();
            return Sanitize(settings);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }

    private static AppSettings Sanitize(AppSettings settings)
    {
        settings.DefaultBrushSize = Math.Clamp(settings.DefaultBrushSize, 4, 160);
        settings.MaxHistory = Math.Clamp(settings.MaxHistory, 5, 100);
        return settings;
    }
}
