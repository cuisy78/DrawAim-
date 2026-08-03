param(
    [string]$ApplicationPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class DrawAimWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(
        IntPtr handle,
        int x,
        int y,
        int width,
        int height,
        bool repaint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

function Wait-DrawAimWindow {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds = 15000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        if ($Process.HasExited) {
            throw "DrawAim exited before creating a window. Exit code: $($Process.ExitCode)"
        }

        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                $Process.Id)
            $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Children,
                $condition)
            if ($null -ne $window) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'Timed out waiting for the DrawAim main window.'
}

function Invoke-AutomationButton {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )

    $buttons = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))

    foreach ($button in $buttons) {
        if ($button.Current.IsEnabled -and $button.Current.AutomationId -eq $AutomationId) {
            $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
            Start-Sleep -Milliseconds 350
            return
        }
    }

    $available = ($buttons | ForEach-Object { $_.Current.AutomationId }) -join ', '
    throw "Could not find button '$AutomationId'. Available ids: $available"
}

function Get-AutomationElementById {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )

    $element = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    if ($null -eq $element) {
        throw "Could not find automation element '$AutomationId'."
    }

    return $element
}

function Get-AutomationTextValue {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )

    $element = Get-AutomationElementById -Window $Window -AutomationId $AutomationId
    try {
        $pattern = [System.Windows.Automation.ValuePattern]$element.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
    }
    catch {
        throw "Element '$AutomationId' does not expose ValuePattern. Control type: $($element.Current.ControlType.ProgrammaticName). $($_.Exception.Message)"
    }

    $value = $pattern.Current.Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Element '$AutomationId' returned an empty text value."
    }

    return $value.Trim()
}

function Invoke-AutomationButtonByNameFragment {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$NameFragment
    )

    $buttons = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))

    $matches = @($buttons | Where-Object {
        $_.Current.IsEnabled -and
        $_.Current.Name.IndexOf($NameFragment, [StringComparison]::Ordinal) -ge 0
    })
    if ($matches.Count -ne 1) {
        $available = ($buttons | ForEach-Object { "'$($_.Current.Name)' [$($_.Current.AutomationId)]" }) -join ', '
        throw "Expected one enabled button containing name '$NameFragment', found $($matches.Count). Available buttons: $available"
    }

    $pattern = $matches[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
    Start-Sleep -Milliseconds 350
}

function Set-AutomationToggle {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][bool]$Checked
    )

    $element = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    if ($null -eq $element -or -not $element.Current.IsEnabled) {
        throw "Could not find enabled toggle '$AutomationId'."
    }

    $pattern = [System.Windows.Automation.TogglePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $isChecked = $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($isChecked -ne $Checked) {
        $pattern.Toggle()
        Start-Sleep -Milliseconds 200
    }
}

function Set-AutomationRangeValue {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][double]$Value
    )

    $element = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    if ($null -eq $element -or -not $element.Current.IsEnabled) {
        throw "Could not find enabled range '$AutomationId'."
    }

    $pattern = [System.Windows.Automation.RangeValuePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $pattern.SetValue($Value)
    Start-Sleep -Milliseconds 150
}

function Assert-AutomationToggle {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][bool]$Expected
    )

    $element = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    if ($null -eq $element) {
        throw "Could not find toggle '$AutomationId' for assertion."
    }

    $pattern = [System.Windows.Automation.TogglePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $actual = $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($actual -ne $Expected) {
        throw "Toggle '$AutomationId' persistence mismatch. Expected $Expected, actual $actual."
    }
}

function Assert-AutomationRangeValue {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][double]$Expected
    )

    $element = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    if ($null -eq $element) {
        throw "Could not find range '$AutomationId' for assertion."
    }

    $pattern = [System.Windows.Automation.RangeValuePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    if ([Math]::Abs($pattern.Current.Value - $Expected) -gt 0.01) {
        throw "Range '$AutomationId' persistence mismatch. Expected $Expected, actual $($pattern.Current.Value)."
    }
}

function Start-DrawAimApplication {
    param(
        [Parameter(Mandatory = $true)][string]$Application,
        [Parameter(Mandatory = $true)][string]$Dotnet
    )

    if ([System.IO.Path]::GetExtension($Application) -ieq '.exe') {
        return Start-Process `
            -FilePath $Application `
            -WorkingDirectory (Split-Path -Parent $Application) `
            -WindowStyle Normal `
            -PassThru
    }

    return Start-Process `
        -FilePath $Dotnet `
        -ArgumentList @($Application) `
        -WorkingDirectory (Split-Path -Parent $Application) `
        -WindowStyle Normal `
        -PassThru
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $Process.Refresh()
    $rect = New-Object DrawAimWindowCapture+RECT
    if (-not [DrawAimWindowCapture]::GetWindowRect($Process.MainWindowHandle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 640 -or $height -lt 480) {
        throw "Unexpected main window size: ${width}x${height}"
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $rect.Left,
            $rect.Top,
            0,
            0,
            (New-Object System.Drawing.Size($width, $height)))
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Set-DrawAimWindowSize {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [int]$Width,
        [int]$Height
    )

    $Process.Refresh()
    if (-not [DrawAimWindowCapture]::MoveWindow(
        $Process.MainWindowHandle,
        8,
        8,
        $Width,
        $Height,
        $true)) {
        throw "MoveWindow failed for ${Width}x${Height}."
    }

    Start-Sleep -Milliseconds 300
    $rect = New-Object DrawAimWindowCapture+RECT
    if (-not [DrawAimWindowCapture]::GetWindowRect($Process.MainWindowHandle, [ref]$rect)) {
        throw 'GetWindowRect failed after MoveWindow.'
    }

    $actualWidth = $rect.Right - $rect.Left
    $actualHeight = $rect.Bottom - $rect.Top
    if ([Math]::Abs($actualWidth - $Width) -gt 8 -or
        [Math]::Abs($actualHeight - $Height) -gt 8) {
        throw "Window size mismatch. Requested ${Width}x${Height}, actual ${actualWidth}x${actualHeight}."
    }
}

function Invoke-RelativeClick {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [double]$X,
        [double]$Y
    )

    $Process.Refresh()
    $rect = New-Object DrawAimWindowCapture+RECT
    [DrawAimWindowCapture]::GetWindowRect($Process.MainWindowHandle, [ref]$rect) | Out-Null
    $screenX = [int]($rect.Left + (($rect.Right - $rect.Left) * $X))
    $screenY = [int]($rect.Top + (($rect.Bottom - $rect.Top) * $Y))
    [DrawAimWindowCapture]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    [DrawAimWindowCapture]::SetCursorPos($screenX, $screenY) | Out-Null
    [DrawAimWindowCapture]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 35
    [DrawAimWindowCapture]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
}

function Invoke-RelativeStroke {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [double]$StartX,
        [double]$StartY,
        [double]$EndX,
        [double]$EndY,
        [int]$Steps = 36,
        [int]$PostDelayMilliseconds = 180
    )

    $Process.Refresh()
    $rect = New-Object DrawAimWindowCapture+RECT
    [DrawAimWindowCapture]::GetWindowRect($Process.MainWindowHandle, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    [DrawAimWindowCapture]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null

    for ($step = 0; $step -le $Steps; $step++) {
        $amount = $step / [double]$Steps
        $x = [int]($rect.Left + ($width * ($StartX + (($EndX - $StartX) * $amount))))
        $y = [int]($rect.Top + ($height * ($StartY + (($EndY - $StartY) * $amount))))
        [DrawAimWindowCapture]::SetCursorPos($x, $y) | Out-Null
        if ($step -eq 0) {
            [DrawAimWindowCapture]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        }

        Start-Sleep -Milliseconds 8
    }

    [DrawAimWindowCapture]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $PostDelayMilliseconds
}

function Invoke-AutomationElementStroke {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][double]$StartX,
        [Parameter(Mandatory = $true)][double]$StartY,
        [Parameter(Mandatory = $true)][double]$EndX,
        [Parameter(Mandatory = $true)][double]$EndY,
        [int]$Steps = 40,
        [int]$PostDelayMilliseconds = 180,
        [double]$FallbackWindowStartX = [double]::NaN,
        [double]$FallbackWindowStartY = [double]::NaN,
        [double]$FallbackWindowEndX = [double]::NaN,
        [double]$FallbackWindowEndY = [double]::NaN
    )

    foreach ($coordinate in @($StartX, $StartY, $EndX, $EndY)) {
        if ([double]::IsNaN($coordinate) -or [double]::IsInfinity($coordinate) -or
            $coordinate -lt 0 -or $coordinate -gt 1) {
            throw "Stroke coordinate fractions for '$AutomationId' must be finite and within 0..1."
        }
    }

    if ($Steps -lt 2) {
        throw "Stroke step count for '$AutomationId' must be at least 2. Actual: $Steps"
    }

    $element = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)))
    $useFallback = $null -eq $element
    if (-not $useFallback) {
        $bounds = $element.Current.BoundingRectangle
        $useFallback = $element.Current.IsOffscreen -or
            $bounds.IsEmpty -or $bounds.Width -lt 100 -or $bounds.Height -lt 100
    }

    if ($useFallback) {
        $fallbackCoordinates = @(
            $FallbackWindowStartX,
            $FallbackWindowStartY,
            $FallbackWindowEndX,
            $FallbackWindowEndY)
        if (@($fallbackCoordinates | Where-Object {
            [double]::IsNaN($_) -or [double]::IsInfinity($_) -or $_ -lt 0 -or $_ -gt 1
        }).Count -gt 0) {
            throw "Drawing element '$AutomationId' has no usable UI Automation bounds and valid window-relative fallback coordinates were not supplied."
        }

        Invoke-RelativeStroke `
            -Process $Process `
            -StartX $FallbackWindowStartX `
            -StartY $FallbackWindowStartY `
            -EndX $FallbackWindowEndX `
            -EndY $FallbackWindowEndY `
            -Steps $Steps `
            -PostDelayMilliseconds $PostDelayMilliseconds
        return
    }

    $Process.Refresh()
    [DrawAimWindowCapture]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    for ($step = 0; $step -le $Steps; $step++) {
        $amount = $step / [double]$Steps
        $x = [int][Math]::Round($bounds.Left + ($bounds.Width * ($StartX + (($EndX - $StartX) * $amount))))
        $y = [int][Math]::Round($bounds.Top + ($bounds.Height * ($StartY + (($EndY - $StartY) * $amount))))
        [DrawAimWindowCapture]::SetCursorPos($x, $y) | Out-Null
        if ($step -eq 0) {
            [DrawAimWindowCapture]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        }

        Start-Sleep -Milliseconds 8
    }

    [DrawAimWindowCapture]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $PostDelayMilliseconds
}

function Assert-Mode2StrokeThemeRecolor {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$LightScreenshotPath,
        [Parameter(Mandatory = $true)][string]$DarkScreenshotPath,
        [double]$StartX = 0.24,
        [double]$StartY = 0.34,
        [double]$EndX = 0.76,
        [double]$EndY = 0.66,
        [double]$FallbackWindowStartX = 0.63,
        [double]$FallbackWindowStartY = 0.47,
        [double]$FallbackWindowEndX = 0.77,
        [double]$FallbackWindowEndY = 0.62
    )

    if (-not (Test-Path -LiteralPath $LightScreenshotPath) -or
        -not (Test-Path -LiteralPath $DarkScreenshotPath)) {
        throw "Mode 2 theme screenshots are missing. Light='$LightScreenshotPath', dark='$DarkScreenshotPath'."
    }

    $canvas = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'CanvasMode2')))
    $useFallback = $null -eq $canvas
    if (-not $useFallback) {
        $canvasBounds = $canvas.Current.BoundingRectangle
        $useFallback = $canvas.Current.IsOffscreen -or
            $canvasBounds.IsEmpty -or $canvasBounds.Width -lt 100 -or $canvasBounds.Height -lt 100
    }

    $Process.Refresh()
    $windowRect = New-Object DrawAimWindowCapture+RECT
    if (-not [DrawAimWindowCapture]::GetWindowRect($Process.MainWindowHandle, [ref]$windowRect)) {
        throw 'GetWindowRect failed during the Mode 2 recolor assertion.'
    }

    $lightBitmap = New-Object System.Drawing.Bitmap($LightScreenshotPath)
    $darkBitmap = New-Object System.Drawing.Bitmap($DarkScreenshotPath)
    try {
        if ($lightBitmap.Width -ne $darkBitmap.Width -or $lightBitmap.Height -ne $darkBitmap.Height) {
            throw "Mode 2 theme screenshot sizes differ. Light=$($lightBitmap.Width)x$($lightBitmap.Height), dark=$($darkBitmap.Width)x$($darkBitmap.Height)."
        }

        if ($useFallback) {
            $canvasLeft = 0
            $canvasTop = 0
            $canvasWidth = $lightBitmap.Width
            $canvasHeight = $lightBitmap.Height
            $pathStartX = $FallbackWindowStartX
            $pathStartY = $FallbackWindowStartY
            $pathEndX = $FallbackWindowEndX
            $pathEndY = $FallbackWindowEndY
        }
        else {
            $canvasLeft = $canvasBounds.Left - $windowRect.Left
            $canvasTop = $canvasBounds.Top - $windowRect.Top
            $canvasWidth = $canvasBounds.Width
            $canvasHeight = $canvasBounds.Height
            $pathStartX = $StartX
            $pathStartY = $StartY
            $pathEndX = $EndX
            $pathEndY = $EndY
        }

        $sampleCount = 31
        $searchRadius = 7
        $lightInkMatches = 0
        $darkInkMatches = 0
        for ($sample = 0; $sample -lt $sampleCount; $sample++) {
            $amount = $sample / [double]($sampleCount - 1)
            $expectedX = [int][Math]::Round(
                $canvasLeft + ($canvasWidth * ($pathStartX + (($pathEndX - $pathStartX) * $amount))))
            $expectedY = [int][Math]::Round(
                $canvasTop + ($canvasHeight * ($pathStartY + (($pathEndY - $pathStartY) * $amount))))
            $foundDarkInkOnLightCanvas = $false
            $foundLightInkOnDarkCanvas = $false
            for ($offsetY = -$searchRadius; $offsetY -le $searchRadius; $offsetY++) {
                for ($offsetX = -$searchRadius; $offsetX -le $searchRadius; $offsetX++) {
                    $pixelX = $expectedX + $offsetX
                    $pixelY = $expectedY + $offsetY
                    if ($pixelX -lt 0 -or $pixelY -lt 0 -or
                        $pixelX -ge $lightBitmap.Width -or $pixelY -ge $lightBitmap.Height) {
                        continue
                    }

                    if (-not $foundDarkInkOnLightCanvas) {
                        $pixel = $lightBitmap.GetPixel($pixelX, $pixelY)
                        $luminance = (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B)
                        $foundDarkInkOnLightCanvas = $luminance -le 90
                    }

                    if (-not $foundLightInkOnDarkCanvas) {
                        $pixel = $darkBitmap.GetPixel($pixelX, $pixelY)
                        $luminance = (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B)
                        $foundLightInkOnDarkCanvas = $luminance -ge 190
                    }

                    if ($foundDarkInkOnLightCanvas -and $foundLightInkOnDarkCanvas) {
                        break
                    }
                }

                if ($foundDarkInkOnLightCanvas -and $foundLightInkOnDarkCanvas) {
                    break
                }
            }

            if ($foundDarkInkOnLightCanvas) {
                $lightInkMatches++
            }

            if ($foundLightInkOnDarkCanvas) {
                $darkInkMatches++
            }
        }

        # Sample beside the injected path. These points are guaranteed to be
        # inside the same answer canvas even when WPF returns rounded UIA bounds.
        $backgroundSampleCount = 21
        $pathDeltaX = $canvasWidth * ($pathEndX - $pathStartX)
        $pathDeltaY = $canvasHeight * ($pathEndY - $pathStartY)
        $pathLength = [Math]::Sqrt(($pathDeltaX * $pathDeltaX) + ($pathDeltaY * $pathDeltaY))
        if ($pathLength -lt 1) {
            throw 'Mode 2 background assertion received a degenerate stroke path.'
        }

        $normalX = (-$pathDeltaY / $pathLength) * 24
        $normalY = ($pathDeltaX / $pathLength) * 24
        $lightBackgroundMatches = 0
        $darkBackgroundMatches = 0
        for ($sample = 0; $sample -lt $backgroundSampleCount; $sample++) {
            $amount = ($sample + 0.5) / [double]$backgroundSampleCount
            $pathX = $canvasLeft + ($canvasWidth * ($pathStartX + (($pathEndX - $pathStartX) * $amount)))
            $pathY = $canvasTop + ($canvasHeight * ($pathStartY + (($pathEndY - $pathStartY) * $amount)))
            $matchedLight = $false
            $matchedDark = $false
            foreach ($side in @(-1, 1)) {
                $sampleX = [int][Math]::Round($pathX + ($side * $normalX))
                $sampleY = [int][Math]::Round($pathY + ($side * $normalY))
                if ($sampleX -lt 0 -or $sampleY -lt 0 -or
                    $sampleX -ge $lightBitmap.Width -or $sampleY -ge $lightBitmap.Height) {
                    continue
                }

                $lightPixel = $lightBitmap.GetPixel($sampleX, $sampleY)
                $darkPixel = $darkBitmap.GetPixel($sampleX, $sampleY)
                $lightLuminance =
                    (0.2126 * $lightPixel.R) + (0.7152 * $lightPixel.G) + (0.0722 * $lightPixel.B)
                $darkLuminance =
                    (0.2126 * $darkPixel.R) + (0.7152 * $darkPixel.G) + (0.0722 * $darkPixel.B)
                $matchedLight = $matchedLight -or $lightLuminance -ge 245
                $matchedDark = $matchedDark -or $darkLuminance -le 55
            }

            if ($matchedLight) {
                $lightBackgroundMatches++
            }

            if ($matchedDark) {
                $darkBackgroundMatches++
            }
        }

        $minimumMatches = [int][Math]::Ceiling($sampleCount * 0.60)
        if ($lightInkMatches -lt $minimumMatches -or $darkInkMatches -lt $minimumMatches) {
            throw "Mode 2 committed-stroke theme recolor failed. Required at least $minimumMatches/$sampleCount path samples in each screenshot; light-theme dark ink=$lightInkMatches, dark-theme light ink=$darkInkMatches. Screenshots: '$LightScreenshotPath', '$DarkScreenshotPath'."
        }

        $minimumBackgroundMatches = 18
        if ($lightBackgroundMatches -lt $minimumBackgroundMatches -or
            $darkBackgroundMatches -lt $minimumBackgroundMatches) {
            throw "Mode 2 canvas background theme switch failed. Required at least $minimumBackgroundMatches/$backgroundSampleCount path-adjacent samples; light background luminance >=245 matched $lightBackgroundMatches, dark background luminance <=55 matched $darkBackgroundMatches. Screenshots: '$LightScreenshotPath', '$DarkScreenshotPath'."
        }
    }
    finally {
        $lightBitmap.Dispose()
        $darkBitmap.Dispose()
    }
}

$caseId = "case-$([Guid]::NewGuid().ToString('N'))"
$dataRoot = Join-Path $script:ProjectRoot ".smoke-data\$caseId"
$outputRoot = Join-Path $script:ProjectRoot "artifacts\smoke\$caseId"
$appDll = if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    Join-Path $script:ProjectRoot 'src\DrawAim.App\bin\Release\net10.0-windows\DrawAim.dll'
}
else {
    [System.IO.Path]::GetFullPath($ApplicationPath)
}

if (-not (Test-Path -LiteralPath $appDll)) {
    throw "Release application not found: $appDll"
}

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$previousDataRoot = $env:DRAWAIM_DATA_ROOT
$env:DRAWAIM_DATA_ROOT = $dataRoot
$process = $null

try {
    $process = Start-DrawAimApplication -Application $appDll -Dotnet $script:DotnetExe

    $window = Wait-DrawAimWindow -Process $process
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'first-run.png')
    Invoke-AutomationButton -Window $window -AutomationId 'GuideStart'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'home.png')
    Invoke-AutomationButton -Window $window -AutomationId 'ToggleTheme'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'home-light-theme.png')
    Invoke-AutomationButton -Window $window -AutomationId 'ToggleTheme'

    Set-DrawAimWindowSize -Process $process -Width 900 -Height 480
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'home-900x480.png')
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $workWidth = [Math]::Max(960, $workArea.Width - 16)
    $workHeight = [Math]::Max(520, $workArea.Height - 16)
    Set-DrawAimWindowSize -Process $process -Width $workWidth -Height $workHeight
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'home-primary-workarea.png')
    Set-DrawAimWindowSize -Process $process -Width 1400 -Height 850

    Invoke-AutomationButton -Window $window -AutomationId 'NavMode1'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode1.png')
    Assert-AutomationToggle -Window $window -AutomationId 'Mode1LockSeed' -Expected $false
    $mode1SeedBeforeNewQuestion = Get-AutomationTextValue -Window $window -AutomationId 'Mode1Seed'
    Invoke-AutomationButton -Window $window -AutomationId 'NewMode1'
    $mode1SeedAfterNewQuestion = Get-AutomationTextValue -Window $window -AutomationId 'Mode1Seed'
    if ([string]::Equals(
        $mode1SeedBeforeNewQuestion,
        $mode1SeedAfterNewQuestion,
        [StringComparison]::Ordinal)) {
        throw "Unlocked Mode 1 Seed did not change after NewMode1. Before='$mode1SeedBeforeNewQuestion', after='$mode1SeedAfterNewQuestion', toggle='Mode1LockSeed'."
    }

    Set-DrawAimWindowSize -Process $process -Width 900 -Height 480
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode1-900x480.png')
    Set-DrawAimWindowSize -Process $process -Width 1400 -Height 850
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1StraightWeight' -Value 60
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1CWeight' -Value 30
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1SWeight' -Value 10
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1DirectionMin' -Value 45
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1DirectionMax' -Value 90
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1AnswerWidth' -Value 12
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode1TargetWidth' -Value 3
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1AnswerWidth' -Expected 12
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1TargetWidth' -Expected 3
    Invoke-AutomationButton -Window $window -AutomationId 'NewMode1'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode1-custom-settings.png')
    Invoke-AutomationElementStroke `
        -Process $process `
        -Window $window `
        -AutomationId 'CanvasMode1' `
        -StartX 0.28 `
        -StartY 0.32 `
        -EndX 0.72 `
        -EndY 0.62 `
        -PostDelayMilliseconds 120 `
        -FallbackWindowStartX 0.40 `
        -FallbackWindowStartY 0.33 `
        -FallbackWindowEndX 0.66 `
        -FallbackWindowEndY 0.54
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode1-after-answer.png')
    Start-Sleep -Milliseconds 750

    Invoke-AutomationButton -Window $window -AutomationId 'ToggleTheme'
    Invoke-AutomationButton -Window $window -AutomationId 'NavMode2'
    Invoke-AutomationElementStroke `
        -Process $process `
        -Window $window `
        -AutomationId 'CanvasMode2' `
        -StartX 0.24 `
        -StartY 0.34 `
        -EndX 0.76 `
        -EndY 0.66 `
        -FallbackWindowStartX 0.63 `
        -FallbackWindowStartY 0.47 `
        -FallbackWindowEndX 0.77 `
        -FallbackWindowEndY 0.62
    $mode2LightStrokeScreenshot = Join-Path $outputRoot 'mode2-light-theme-committed-stroke.png'
    Save-WindowScreenshot -Process $process -Path $mode2LightStrokeScreenshot
    Invoke-AutomationButton -Window $window -AutomationId 'ToggleTheme'
    $mode2DarkStrokeScreenshot = Join-Path $outputRoot 'mode2-dark-theme-recolored-stroke.png'
    Save-WindowScreenshot -Process $process -Path $mode2DarkStrokeScreenshot
    Assert-Mode2StrokeThemeRecolor `
        -Process $process `
        -Window $window `
        -LightScreenshotPath $mode2LightStrokeScreenshot `
        -DarkScreenshotPath $mode2DarkStrokeScreenshot
    Invoke-AutomationButton -Window $window -AutomationId 'ClearMode2'
    Invoke-AutomationButton -Window $window -AutomationId 'NavHome'
    Invoke-AutomationButton -Window $window -AutomationId 'NavMode2'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode2.png')
    Set-DrawAimWindowSize -Process $process -Width 900 -Height 480
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode2-900x480.png')
    Set-DrawAimWindowSize -Process $process -Width 1400 -Height 850
    Set-AutomationToggle -Window $window -AutomationId 'Mode2UseCountRange' -Checked $true
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode2MinCount' -Value 1
    Set-AutomationRangeValue -Window $window -AutomationId 'Mode2MaxCount' -Value 10
    Invoke-AutomationButton -Window $window -AutomationId 'NewMode2'
    Invoke-RelativeStroke -Process $process -StartX 0.63 -StartY 0.47 -EndX 0.77 -EndY 0.62
    Invoke-AutomationButton -Window $window -AutomationId 'UndoMode2'
    Invoke-AutomationButton -Window $window -AutomationId 'RedoMode2'
    Invoke-AutomationButton -Window $window -AutomationId 'ClearMode2'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode2-after-clear.png')
    Invoke-RelativeStroke -Process $process -StartX 0.63 -StartY 0.47 -EndX 0.77 -EndY 0.62
    Invoke-AutomationButton -Window $window -AutomationId 'SubmitMode2'
    Start-Sleep -Milliseconds 1050
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode2-after-submit.png')

    Invoke-AutomationButton -Window $window -AutomationId 'NavMode3'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode3.png')
    Set-DrawAimWindowSize -Process $process -Width 900 -Height 480
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode3-900x480.png')
    Set-DrawAimWindowSize -Process $process -Width 1400 -Height 850
    Set-AutomationToggle -Window $window -AutomationId 'Mode3PracticeMode' -Checked $false
    Invoke-AutomationButton -Window $window -AutomationId 'NewMode3'
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode3-test-before-submit.png')
    Invoke-RelativeClick -Process $process -X 0.30 -Y 0.32
    Invoke-RelativeStroke -Process $process -StartX 0.49 -StartY 0.40 -EndX 0.72 -EndY 0.58
    Invoke-AutomationButton -Window $window -AutomationId 'SubmitMode3'
    Start-Sleep -Milliseconds 350
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'mode3-after-submit.png')

    $windowPattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    ([System.Windows.Automation.WindowPattern]$windowPattern).Close()
    if (-not $process.WaitForExit(10000)) {
        throw 'DrawAim did not exit within 10 seconds after a normal close request.'
    }

    if ($process.ExitCode -ne 0) {
        throw "Unexpected DrawAim exit code: $($process.ExitCode)"
    }

    $process = Start-DrawAimApplication -Application $appDll -Dotnet $script:DotnetExe
    $window = Wait-DrawAimWindow -Process $process
    Invoke-AutomationButton -Window $window -AutomationId 'NavMode1'
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1StraightWeight' -Expected 60
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1CWeight' -Expected 30
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1SWeight' -Expected 10
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1DirectionMin' -Expected 45
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1DirectionMax' -Expected 90
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1AnswerWidth' -Expected 12
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode1TargetWidth' -Expected 3
    Invoke-AutomationButton -Window $window -AutomationId 'NavMode2'
    Assert-AutomationToggle -Window $window -AutomationId 'Mode2UseCountRange' -Expected $true
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode2MinCount' -Expected 1
    Assert-AutomationRangeValue -Window $window -AutomationId 'Mode2MaxCount' -Expected 10
    Invoke-AutomationButton -Window $window -AutomationId 'NavMode3'
    Assert-AutomationToggle -Window $window -AutomationId 'Mode3PracticeMode' -Expected $false
    Save-WindowScreenshot -Process $process -Path (Join-Path $outputRoot 'settings-restored-after-restart.png')

    $windowPattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    ([System.Windows.Automation.WindowPattern]$windowPattern).Close()
    if (-not $process.WaitForExit(10000)) {
        throw 'DrawAim restart did not exit within 10 seconds.'
    }

    if ($process.ExitCode -ne 0) {
        throw "Unexpected DrawAim restart exit code: $($process.ExitCode)"
    }

    $settingsPath = Join-Path $dataRoot 'settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        throw "Settings file was not written: $settingsPath"
    }

    $savedSettings = Get-Content -LiteralPath $settingsPath -Raw -Encoding utf8 | ConvertFrom-Json
    $expectedGeneratorVersions = @{
        modeOne = 'LineGeneratorV2'
        modeTwo = 'MultiLineGeneratorV2'
        modeThree = 'ColorGeneratorV1'
    }
    foreach ($modeName in $expectedGeneratorVersions.Keys) {
        $actualGeneratorVersion = $savedSettings.$modeName.generatorVersion
        if (-not [string]::Equals(
            $actualGeneratorVersion,
            $expectedGeneratorVersions[$modeName],
            [StringComparison]::Ordinal)) {
            throw "Saved generator version mismatch for '$modeName'. Expected '$($expectedGeneratorVersions[$modeName])', actual '$actualGeneratorVersion'."
        }
    }

    Write-Host 'GUI_SMOKE_OK'
    Write-Host "Screenshots: $outputRoot"
    Write-Host "Smoke data: $dataRoot"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        $process.WaitForExit(3000) | Out-Null
    }

    $env:DRAWAIM_DATA_ROOT = $previousDataRoot
}
