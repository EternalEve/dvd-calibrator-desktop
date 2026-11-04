using System.Diagnostics;
using System.IO;

namespace DvdCalibrator.App;

public sealed partial class MainWindow
{
    private void OpenBeginnerGuide()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "新手教程.html");
        if (!File.Exists(path)) throw new IOException("找不到随程序提供的新手教程，请完整解压便携包后重试。");
        var uri = new UriBuilder(new Uri(path)) { Fragment = "step-" + (int)CurrentStep };
        try { Process.Start(new ProcessStartInfo(uri.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception e) { throw new IOException("无法打开浏览器。请在程序目录中手动打开“新手教程.html”。", e); }
    }

    private const string FineDiscGenerationSteps = "② “标定”页面，找到“生成标定盘”页面，确认显示的内外半径正确，选择位置并点击“生成镜像/音轨”。\n③ 拍摄第二张记录面。回到本工具，下一步提交 ISO 同目录、同名的 .calibration.json（位于②选择的位置）和照片。";

    private sealed record StepTutorial(string Purpose, string Outcome, string Location, string Actions, string Guide);
    private static StepTutorial Tutorial(CalibrationStep step, bool dense = false) => step switch
    {
        CalibrationStep.Prepare => new(
            "先制作一张有已知图案的测试盘，再用照片测量它刻出来的形状。",
            "第一张盘的生成记录与对应实拍照片；两项导入后可继续。",
            "先用 DiscImageStudio 生成镜像、用刻录软件写盘，再回本工具导入。",
            "① 保存黑白标定图，在 DiscImageStudio 用默认 DVD 参数生成并刻录。\n② 选择 ISO 同目录的 .iso.json，再选择盘片照片。",
            "第一张请使用本工具提供的黑白标定图并保持居中。ISO 用于刻录，同目录的 .iso.json 记录本次生成参数。\n拍有图案的记录面，保留完整盘边和中心孔。已经保存过进度时用右上角“打开任务”。"),
        CalibrationStep.CoarseRectify => RectificationTutorial(false),
        CalibrationStep.FineRectify => RectificationTutorial(true),
        CalibrationStep.CoarseFit when dense => new(
            "识别黑白半盘图案，计算第一轮补偿参数。",
            "受支持的初步补偿参数，接着用它准备第二张。",
            "本工具 · 当前显示本次黑白半盘照片。",
            "① 点“分析密环 / 混合”，等待计算。\n② 放大检查紫色预测线是否沿着清楚区域的条纹边界。\n③ 提示得到受支持结果后，点下一步制作第二张。",
            "半盘图案在刻录几何不匹配时会呈现密集明暗变化。本工具按内置黑白半盘图和记录中的生成参数识别照片。\n紫线是完整模型预测，蓝线只表示能读到的观测片段；遮挡或缺失区域里的紫线尚需核对。提示存在多个冲突解释或信息不足时不能直接继续，按诊断补充信息。"),
        CalibrationStep.CoarseFit => new(
            "让工具知道照片中一条十字臂的位置，计算第一轮补偿。",
            "经过你核对的初步补偿参数，接着用它制作第二张盘。",
            "本工具 · 在左侧实拍原照片上点选。",
            "① 点“描主线”，沿同一条十字臂的中心从内向外点至少 24 点。\n② 点“拟合描线”；检查橙线沿途是否贴着照片中的这条线。\n③ 吻合后点“确认候选曲线”，再点下一步。",
            "选择一条从内圈延伸到外圈、看得清的十字臂。追踪细线的中央，不是两边的轮廓，也不是同心圆。刻出来弯了甚至绕圈是待测现象，沿照片中的真实轨迹点，不要人为拉直。主线须覆盖记录环带径向宽度的 60% 以上；弯曲快处加密，不跳到相邻臂。\n拟合是用参数解释这些点。候选是可能的解释；应检查整条橙线，尤其是点之间是否绕错一圈。程序把最外侧一段点留作检查，这叫“留出”，不是漏算；不要删掉这些点。\n反光挡住曲线时，用“标反光遮罩”圈出区域并“结束遮罩”；大段看不清应重新拍。可用“描对侧检查线”补充同一条直径另一端的臂，两条相邻臂不算对侧。\n高级形状选项先保留默认。本工具要求 24 点，不能照搬原项目最低点数。半盘密环入口只用于已知解析靶及相应软件示例，普通实拍十字盘走描线流程。"),
        CalibrationStep.Plan => new(
            "用选用的参数刻录辅助标定盘，以便精调。",
            "用本页参数刻录的辅助标定盘，以及它的记录和照片。",
            "本页查看参数；去 DiscImageStudio 生成镜像，再用刻录软件写盘。",
            "① 将本页参数填入 DiscImageStudio 的“工程设置”。\n" + FineDiscGenerationSteps,
            "点“复制参数”可复制整组数值。已有参数时，可点“编辑 / 使用已有参数”，粘贴、打开参数文件或选用预设。\n在“生成标定盘”核对内外半径后生成镜像，再用刻录软件写入第二张空白盘。拍摄完整记录面，保留外缘、中心孔和清晰十字线。\n可展开“预计图案与拍摄清晰度”查看预测。预计像素/mm 默认 8；若提示线太细，需改善拍摄安排。右上角“保存进度”可保存当前任务。"),
        CalibrationStep.FineImport => new(
            "把刚刚制作的第二张记录和照片交回工具，核对有没有用错参数。",
            "与第 4 步计划匹配的第二张照片。",
            "回到本工具；所需文件来自第 4 步的实际生成和拍摄。",
            "① 首次导入点“导入第二张照片”，依次选择 .calibration.json 和实拍照片。\n② 显示第二张已导入后，点下一步。",
            "在原项目所选的 ISO 输出目录找到同名 .calibration.json。\n选错照片或补拍后，可在本页更换或移除照片。更换生成记录时，请同时选择与新记录对应的照片。\n如果提示参数不匹配，检查第 4 页的参数是否完整填入 DiscImageStudio，并确认所选记录对应第二张。"),
        CalibrationStep.FineFit => new(
            "再测一次第二张的实际曲线，计算最终补偿；第二张即使仍有弯曲也可以测量。",
            "第二张独立测出的补偿参数，下一步检查它与第一张是否相符。",
            "本工具 · 当前显示的是第二张实拍原照片。",
            "① 点“自动描线并拟合”，核对自动描点和橙线。\n② 请自行删除偏差较大的点。修改后点“拟合描线”。\n③ 核对后点“确认候选曲线”。",
            "若自动描线失败，可沿一条十字臂的中心由内向外手动描点，主线至少 24 点，覆盖记录环带径向宽度的 60% 以上。可补描同一直径的对侧臂作检查。\n有反光时可先标遮罩，结束遮罩后重新自动描线。核对整条橙线是否贴着照片中的十字臂。"),
        _ => new(
            "核对两张盘的测量，查看最终补偿参数。",
            "可复制的补偿参数。",
            "在本页查看和复制；到 DiscImageStudio 的“工程设置”填入。",
            "① 进入本页后自动检查两盘一致性。\n② 检查通过后复制参数，在 DiscImageStudio 的“工程设置”填入。\n③ 到“设计”页添加图片并生成新镜像。",
            "可逐项复制，或点“复制补偿参数”复制整组数值。需要保留本次测量时，点右上角“保存进度”。\n若检查未通过，按本页提示核对生成记录、盘片和照片。")
    };

    private static StepTutorial RectificationTutorial(bool fine) => new(
        fine ? "第二张拍摄角度可能不同，需要重新把它的照片转换成正面视图。" : "照片拍斜了会把圆盘拍成椭圆。先消除拍摄角度影响，再测图案本身的弯曲。",
        "本张照片的已确认正视预览；下一步可以描图案。",
        "本工具 · 在左侧原照片上标点，右侧查看计算后的预览。",
        "① 点“标外缘”，沿盘片最外侧一圈均匀点 8–16 点。\n② 点“标中心孔”，沿真正穿透的小孔均匀点 8–16 点。\n③ 点“计算联合矫正”，检查两圈橙色轮廓，贴合后点“确认矫正”。",
        "选外缘时认实体盘片的最外侧，不选记录区边界、透明夹持环或桌面阴影。选中心孔时认直径 15 mm 的贯通孔，不选外面较大的塑料环。点要分散到上下左右，不能集中在一小段。\n左键空白处加点，拖动已有点修正，Delete 删除选中点。滚轮缩放，右键拖动画面，“适合窗口”找回整盘。标点始终在左侧原照片上；右侧正视/极坐标图只供检查。\n计算后先看两圈橙色投影轮廓是否贴合原照片实体边，再看“正视预览”是否像正面看圆盘。“极坐标预览”是把圆环摊开，初次使用可以先不看。\n此步骤只修正拍摄透视，不会把刻录弯曲拉直；图案仍弯并不等于校正失败。若边缘被遮挡、强反光或广角拍弯，先重拍。本工具当前使用 12 cm 单层 DVD，不沿用原项目 CD、BD 或 8 cm 小盘流程。"
        + (fine ? "\n不要复制第一张的边缘点；第二张需要自己的标注与确认。" : ""));
}
