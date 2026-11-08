# Build KAMCD.dll (own winmm proxy for MCI cdaudio emulation). Requires TinyCC (32-bit tcc.exe).
param([string]$Tcc = 'C:\Retro\Tools\tcc\x\tcc\tcc.exe', [string]$Out = 'C:\Retro\Tools\build\dll\KAMCD.dll')
$src = Join-Path $PSScriptRoot 'winmm_cd.c'
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Remove-Item $Out -EA SilentlyContinue
& $Tcc -shared -o $Out $src
if (-not (Test-Path $Out)) { throw 'tcc build failed' }
# tcc always decorates stdcall exports (_name@N): strip them in the export name table (order stays sorted)
$b = [IO.File]::ReadAllBytes($Out)
$s = [Text.Encoding]::GetEncoding(28591).GetString($b)
$n = 0
foreach ($m in [regex]::Matches($s, '_((?:mci|mixer)\w+)@\d+\x00')) {
  $new = [Text.Encoding]::ASCII.GetBytes($m.Groups[1].Value)
  for ($i = 0; $i -lt $m.Length; $i++) { $b[$m.Index + $i] = if ($i -lt $new.Length) { $new[$i] } else { 0 } }
  $n++
}
[IO.File]::WriteAllBytes($Out, $b)
"built $Out ($((Get-Item $Out).Length) bytes), renamed exports: $n"
