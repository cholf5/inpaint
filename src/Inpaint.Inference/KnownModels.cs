namespace Inpaint.Inference;

public sealed record ModelInfo(string FileName, string Url, string? BackupUrl);

/// <summary>与网页版相同的模型来源（主源 HuggingFace，备源 CDN 代理）。</summary>
public static class KnownModels
{
    public static readonly ModelInfo MiganPipeline = new(
        "migan_pipeline_v2.onnx",
        "https://huggingface.co/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx",
        "https://worker-share-proxy-01f5.lxfater.workers.dev/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx");

    public static readonly ModelInfo RealEsrganX4 = new(
        "realesrgan-x4.onnx",
        "https://huggingface.co/lxfater/inpaint-web/resolve/main/realesrgan-x4.onnx",
        "https://worker-share-proxy-01f5.lxfater.workers.dev/lxfater/inpaint-web/resolve/main/realesrgan-x4.onnx");
}
