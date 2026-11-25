param(
    [string]$IconPath,
    [string]$OutputDirectory,
    [switch]$ListingAssets
)
$ErrorActionPreference = 'Stop'
if (-not $IconPath -and -not $OutputDirectory) { throw 'Supply IconPath and/or OutputDirectory.' }
if ($ListingAssets -and -not $OutputDirectory) { throw 'ListingAssets requires OutputDirectory.' }
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$logoPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PhpDevTray.App/Assets/AppLogo.xaml'
$stream = [IO.File]::OpenRead($logoPath)
try { $drawing = [Windows.Markup.XamlReader]::Load($stream); $drawing.Freeze() }
finally { $stream.Dispose() }
function Get-LogoPng([int]$Size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([Windows.Media.ScaleTransform]::new($Size / 34.0, $Size / 34.0))
        $context.DrawDrawing($drawing)
        $context.Pop()
    }
    finally { $context.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = [IO.MemoryStream]::new()
    try { $encoder.Save($buffer); return ,$buffer.ToArray() }
    finally { $buffer.Dispose() }
}
if ($IconPath) {
    $iconFile = [IO.Path]::GetFullPath($IconPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($iconFile)) -Force | Out-Null
    $sizes = @(16, 20, 24, 28, 32, 40, 48, 64, 96, 128, 256)
    $frames = @($sizes | ForEach-Object { ,(Get-LogoPng $_) })
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($iconFile))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    }
    finally { $writer.Dispose() }
}
if ($OutputDirectory) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $assets = @(
        @{ Name = 'StoreLogo.png'; Size = 50 },
        @{ Name = 'Square44x44Logo.png'; Size = 44 },
        @{ Name = 'Square150x150Logo.png'; Size = 150 }
    )
    if ($ListingAssets) {
        $assets += @{ Name = 'logo-300.png'; Size = 300 }
        $assets += @{ Name = 'logo-1024.png'; Size = 1024 }
    }
    foreach ($asset in $assets) {
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory $asset.Name), (Get-LogoPng $asset.Size))
    }
    if ($ListingAssets) {
        [xml]$source = Get-Content -LiteralPath $logoPath -Raw
        $glyph = $source.SelectSingleNode('//*[local-name()="GeometryDrawing"][@Geometry]').Geometry
        $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 34 34" width="1024" height="1024">
  <rect width="34" height="34" rx="10" fill="#275DD7"/>
  <path transform="translate(6 6) scale(0.916666666666667)" d="$glyph" fill="none" stroke="#FFFFFF" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
"@
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'logo.svg'), $svg, [Text.UTF8Encoding]::new($false))
    }
}
