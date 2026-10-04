using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Inpaint.Tests;

/// <summary>单条目异步数据传输替身（经窗口取得的真实 headless 剪贴板可存放它）。</summary>
/// <remarks>IClipboard 本身带防用户实现哨兵，无法手写替身；预置数据走真实 headless 剪贴板的 SetDataAsync。</remarks>
internal sealed class FakeAsyncDataTransfer(params IAsyncDataTransferItem[] items) : IAsyncDataTransfer
{
    public IReadOnlyList<DataFormat> Formats => items
        .SelectMany(item => item.Formats)
        .Distinct()
        .ToArray();

    public IReadOnlyList<IAsyncDataTransferItem> Items => items;

    public void Dispose()
    {
    }
}

/// <summary>按格式返回固定值的条目替身。</summary>
internal sealed class FakeAsyncDataTransferItem(params (DataFormat Format, object? Value)[] entries)
    : IAsyncDataTransferItem
{
    public IReadOnlyList<DataFormat> Formats => entries.Select(entry => entry.Format).ToArray();

    public Task<object?> TryGetRawAsync(DataFormat format) =>
        Task.FromResult(entries.FirstOrDefault(entry => entry.Format.Equals(format)) is { } hit ? hit.Value : null);
}
