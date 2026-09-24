param([string]$Jxr, [string]$I420, [int]$W, [int]$H)
Add-Type -AssemblyName PresentationCore
function Srgb-Enc([double]$x) { if ($x -le 0.0031308) { $x*12.92 } else { 1.055*[math]::Pow($x, 1/2.4) - 0.055 } }
$fs = [IO.File]::OpenRead($Jxr)
$dec = New-Object Windows.Media.Imaging.WmpBitmapDecoder($fs, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$f = $dec.Frames[0]
$w = $f.PixelWidth; $h = $f.PixelHeight
$stride = $w*16
$buf = [double[]]::new([long]$stride*$h/8)
$f.CopyPixels($buf, $stride, 0)
$fs.Close()
$yuv = [IO.File]::ReadAllBytes($I420)
$yLen = [long]$W*$H
$cW = [int]($W/2)
$sdw = 450.0
$rand = [Random]::new(7)
$sp8 = [double[]]::new(30000)
$spY = [double[]]::new(30000)
for ($i=0; $i -lt 30000; $i++) {
    $x = $rand.Next($w); $y = $rand.Next($h)
    $p = ([long]$y*$w+$x)*4
    $r = [math]::Max($buf[$p],0)*80.0
    $g = [math]::Max($buf[$p+1],0)*80.0
    $b = [math]::Max($buf[$p+2],0)*80.0
    $r8 = [math]::Round(255*[math]::Max(0.0,[math]::Min(1.0,(Srgb-Enc ([math]::Min(1.0, $r/$sdw))))))
    $g8 = [math]::Round(255*[math]::Max(0.0,[math]::Min(1.0,(Srgb-Enc ([math]::Min(1.0, $g/$sdw))))))
    $b8 = [math]::Round(255*[math]::Max(0.0,[math]::Min(1.0,(Srgb-Enc ([math]::Min(1.0, $b/$sdw))))))
    $sp8[$i] = [math]::Max($r8,[math]::Max($g8,$b8)) - [math]::Min($r8,[math]::Min($g8,$b8))

    $Y = ($yuv[[long]$y*$W+$x] - 16)/219.0
    $Cb = ($yuv[$yLen + [long]($y/2)*$cW + [long]($x/2)] - 128)/224.0
    $Cr = ($yuv[$yLen + $cW + [long]($y/2)*$cW + [long]($x/2)] - 128)/224.0
    $rr = $Y + 1.28033*$Cr
    $gg = $Y - 0.21482*$Cb - 0.38059*$Cr
    $bb = $Y + 2.12798*$Cb
    $r8b = [math]::Round(255*[math]::Max(0.0,[math]::Min(1.0,$rr)))
    $g8b = [math]::Round(255*[math]::Max(0.0,[math]::Min(1.0,$gg)))
    $b8b = [math]::Round(255*[math]::Max(0.0,[math]::Min(1.0,$bb)))
    $spY[$i] = [math]::Max($r8b,[math]::Max($g8b,$b8b)) - [math]::Min($r8b,[math]::Min($g8b,$b8b))
}
$s8 = $sp8 | Sort-Object
$sY = $spY | Sort-Object
"rgb8(源)          p50={0} p90={1} p99={2} max={3}" -f [math]::Round($s8[15000]), [math]::Round($s8[27000]), [math]::Round($s8[29700]), [math]::Round($s8[29999])
"I420roundtrip(库) p50={0} p90={1} p99={2} max={3}" -f [math]::Round($sY[15000]), [math]::Round($sY[27000]), [math]::Round($sY[29700]), [math]::Round($sY[29999])
