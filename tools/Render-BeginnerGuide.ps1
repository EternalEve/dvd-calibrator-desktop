#requires -Version 7.0
# Rebuild the offline help from its two maintained Markdown sources. No downloads.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$guide = (ConvertFrom-Markdown -InputObject (Get-Content -LiteralPath (Join-Path $projectRoot 'docs/guided-workflow.md') -Raw)).Html
$materials = (ConvertFrom-Markdown -InputObject (Get-Content -LiteralPath (Join-Path $projectRoot 'docs/material-preparation.md') -Raw)).Html
$guide = [regex]::Replace($guide, '(<h[1-6] id=")([^"]+)', '${1}guide-$2')
$materials = [regex]::Replace($materials, '(<h[1-6] id=")([^"]+)', '${1}materials-$2')
$guide = [regex]::Replace($guide, '<h2[^>]*>(第\s*([1-8])\s*步[^<]*)</h2>', '<h2 id="step-$2">$1</h2>')
$materials = $materials.Replace('href="guided-workflow.md#step-1"', 'href="#step-1"')
$names = @('准备第一张', '校正第一张', '识别黑白盘', '制作精调盘', '提交精调材料', '校正第二张', '计算精调', '使用最终参数')
$navigation = '<a href="#start">两张盘标定流程</a><a href="#materials">第一张的文件位置</a>'
for ($i = 0; $i -lt $names.Count; $i++) { $navigation += '<a href="#step-' + ($i + 1) + '">' + ($i + 1) + '. ' + $names[$i] + '</a>' }
$template = @'
<!doctype html>
<!-- Generated from guided-workflow.md and material-preparation.md. Run tools/Render-BeginnerGuide.ps1. -->
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>DVD Calibrator · 新手教程</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}html{scroll-padding-top:24px}
body{margin:0;background:#11151c;color:#e7ecf3;font:16px/1.85 system-ui,"Microsoft YaHei UI",sans-serif}
nav{position:fixed;inset:0 auto 0 0;width:230px;padding:28px 20px;background:#1a202b;overflow:auto;border-right:1px solid #344155}
nav b{display:block;margin-bottom:14px;color:#fff}nav a{display:block;padding:7px 8px;border-radius:5px;text-decoration:none;color:#bac7d7;font-size:14px}
nav a:hover,nav a:focus{background:#263950;color:#fff}main{max-width:1160px;margin-left:230px;padding:32px 50px 100px}
h1{font-size:29px;line-height:1.5;margin:12px 0 28px}section+section h1{border-top:2px solid #4b87b7;padding-top:48px;margin-top:64px}
h2{font-size:22px;line-height:1.55;margin:48px 0 16px;padding-bottom:10px;border-bottom:1px solid #344155;color:#f3f7ff}
p{margin:14px 0}li{margin:10px 0}a{color:#8fc9ff;text-underline-offset:3px}strong{color:#fff}
code{font:14px/1.65 Consolas,"Microsoft YaHei UI",monospace;background:#202b3a;border-radius:4px;padding:2px 5px;overflow-wrap:anywhere}
pre{background:#0d1117;padding:20px;border:1px solid #344155;border-radius:8px;overflow:auto}
pre code{padding:0;background:none;white-space:pre;overflow-wrap:normal}
table{border-collapse:collapse;width:100%;font-size:14px;line-height:1.8}td,th{border:1px solid #344155;padding:12px 14px;text-align:left;overflow-wrap:anywhere}th{background:#202b3a}
@media(max-width:800px){nav{position:static;width:auto;border-right:0;border-bottom:1px solid #344155}nav a{display:inline-block}main{margin:0;padding:22px}h1{font-size:24px}table{display:block;overflow:auto}}
@media print{nav{display:none}main{margin:0;padding:0;max-width:none}body{background:white;color:#111}h1,h2,strong{color:#111}code,pre,th{background:#eee;color:#111}a{color:#164c80}pre,pre code{white-space:pre-wrap}h2{break-after:avoid}}
</style></head><body><nav><b>DVD CALIBRATOR<br>从第一次使用开始</b>__NAV__</nav><main><section id="start">__GUIDE__</section><section id="materials">__MATERIALS__</section></main></body></html>
'@
$document = $template.Replace('__NAV__', $navigation).Replace('__GUIDE__', $guide).Replace('__MATERIALS__', $materials)
# Stable line endings keep generated output reviewable across platforms.
$document.Replace("`r`n", "`n") | Set-Content -LiteralPath (Join-Path $projectRoot 'docs/beginner-guide.html') -Encoding utf8NoBOM -NoNewline
Write-Output 'Rendered offline beginner guide.'
