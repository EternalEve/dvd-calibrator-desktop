namespace DvdCalibrator.Core;

public static class OutputFolder
{
    public static void Publish(string output, Action<string> write, CancellationToken cancellationToken = default)
    {
        string target = Path.GetFullPath(output);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("输出路径已存在，禁止覆盖旧任务。");
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("不能输出到文件系统根目录。");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".dvd-calibrator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            write(staging);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target); // Fails if a concurrent task has claimed this destination.
        }
        finally
        {
            // Delete only our exact generated staging directory, never the caller's target.
            if (Path.GetDirectoryName(Path.GetFullPath(staging)) == parent
                && Path.GetFileName(staging).StartsWith(".dvd-calibrator-", StringComparison.Ordinal)
                && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
