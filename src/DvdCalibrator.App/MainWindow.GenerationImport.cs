using System.IO;
using System.Text;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;
using Microsoft.Win32;

namespace DvdCalibrator.App;

public sealed partial class MainWindow
{
    private SnapshotBundle? SelectGeneration(string path, bool fine)
    {
        if (new FileInfo(path).Length > RecordJson.MaxBytes) throw new ArgumentException("记录超过 4 MB，请选择生成时的原始参数记录。");
        byte[] bytes = File.ReadAllBytes(path);
        using var doc = RecordJson.ParseDocument(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (doc.RootElement.TryGetProperty("recordType", out _)) return SnapshotBundle.Load(path);
        bool calibration = doc.RootElement.TryGetProperty("target", out _);
        if (!fine)
        {
            if (calibration) throw new ArgumentException("第一张请提交黑白半盘图生成的 .iso.json；辅助标定记录用于第二张精调盘。");
            return SnapshotBundle.ImportHalfDiscRecord(bytes);
        }
        var media = twoDisc?.Plan.Disc;
        if (media is null) return null;
        if (calibration) return SnapshotBundle.ImportCalibrationRecord(path, media);
        _ = DiscImageRecord.ReadBytes(bytes);
        var source = new OpenFileDialog
        {
            Title = "选择第二张实际生成时使用的源图",
            Filter = "生成源图|*.png;*.bmp;*.jpg;*.jpeg;*.gif"
        };
        if (source.ShowDialog(this) != true) return null;
        return SnapshotBundle.ImportIsoRecord(path, source.FileName, media, halfDisc: false);
    }

    private void ExportCoarseSource()
    {
        var dialog = new SaveFileDialog { Title = "保存黑白粗调源图", Filter = "PNG 黑白半盘图|*.png", DefaultExt = ".png", FileName = "黑白粗调.png" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllBytes(dialog.FileName, SnapshotBundle.HalfDiscPng());
        SetStatus("已保存黑白半盘图。在 DiscImageStudio 中用默认 DVD 参数居中铺满绘图区，生成 ISO 并刻录；再提交该 ISO 同目录的 .iso.json 和盘片照片。");
    }
}
