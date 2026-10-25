param(
  [string]$Focus = ""
)

$problem = @"
WORLDGEN 3D FREE-FLIGHT - GIMBAL CONTROL / POLE INSTABILITY PROBLEM
"@

if ($Focus -ne "") {
  $problem += "`n`nFOCUS (from command):`n" + $Focus + "`n"
}

$problem += @"

THE CORE ISSUE
Q/E turning and flight control at high latitudes (near poles) causes wild pitch/nose jumps and erratic behavior. Previous fixes did not resolve it.

ROOT CAUSE - GIMBAL LOCK / EULER ANGLE SINGULARITY
At the poles (lat ~ ±90°), the geographic basis becomes singular:
1. East vector computed via atan2 becomes unstable when rXZ -> 0
2. North = Cross(East, Up) becomes degenerate
3. SetLookDirection() uses this unstable basis - small floating point changes cause pitch to jump
4. View matrix up-vector calculation unstable when basis collapses
5. Per-frame updates re-project orientation using singular basis causing feedback

CURRENT SYMPTOMS
- Tapping Q/E gives inconsistent/random turns
- Holding Q/E makes nose pitch jump up and down wildly
- Worse near poles (lat ~89°+) and when looking steeply up/down

CURRENT STATE
Camera uses Yaw/Pitch (Euler) + geographic basis (North/East/Up). This is fundamentally broken at singularities. Needs quaternion-based orientation or a gimbal-lock-free frame that doesn't depend on geographic north/east at poles.
"@

$problem | Set-Content C:\dev\WorldGen\gemini.txt -Encoding utf8

Add-Content C:\dev\WorldGen\gemini.txt "`n================================================================================`nFILE: src/WorldGen.App/Preview3D/PreviewCamera.cs (FULL - CURRENT STATE)`n================================================================================`n" -Encoding utf8
Add-Content C:\dev\WorldGen\gemini.txt (Get-Content C:\dev\WorldGen\src\WorldGen.App\Preview3D\PreviewCamera.cs -Raw) -Encoding utf8

Add-Content C:\dev\WorldGen\gemini.txt "`n`n================================================================================`nFILE: src/WorldGen.App/Preview3D/Terrain3DHost.cs (KEY HANDLING SECTIONS)`n================================================================================`n" -Encoding utf8
$matches = Get-Content C:\dev\WorldGen\src\WorldGen.App\Preview3D\Terrain3DHost.cs -Raw | Select-String -Pattern "HandleFlightKey|BuildFlightInput|OnFlyTick|KeyboardTurnStep" -Context 0,40
foreach ($m in $matches) {
  Add-Content C:\dev\WorldGen\gemini.txt ($m.Line + "`n")
  if ($m.Context.PostContext) {
    Add-Content C:\dev\WorldGen\gemini.txt ($m.Context.PostContext -join "`n")
  }
  Add-Content C:\dev\WorldGen\gemini.txt "`n---`n"
}

Write-Host "gemini.txt overwritten"
