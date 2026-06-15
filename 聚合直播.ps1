# ================= 配置区域 =================
$workDir = "D:\D-Software\source\C1og-AllLive"
$uwpProcessName = "AllLive.UWP"
$uwpAppId = "5421.24244EC421563_a5x6jjv384fej!App" 
$serviceScriptName = "SignService.ps1"
$launcherMutexName = "Local\C1og-AllLive.Launcher"

# 强制控制台使用 UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# ===========================================

$launcherMutex = $null
$launcherMutexCreated = $false
try {
    $launcherMutex = [System.Threading.Mutex]::new($true, $launcherMutexName, [ref]$launcherMutexCreated)
} catch {
    Write-Host "[错误] 创建启动脚本实例锁失败: $_" -ForegroundColor Red
    Start-Sleep -Seconds 1
    [System.Environment]::Exit(1)
}

if (-not $launcherMutexCreated) {
    Write-Host "[退出] 已有聚合直播启动脚本在运行，关闭当前标签页。" -ForegroundColor Yellow
    if ($launcherMutex) {
        $launcherMutex.Dispose()
    }
    Start-Sleep -Milliseconds 300
    [System.Environment]::Exit(0)
}

$code = @"
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
"@
try {
    Add-Type -MemberDefinition $code -Name "Win32SetForegroundWindow" -Namespace Win32Functions -ErrorAction SilentlyContinue
} catch {}

# --- 函数：检查 SignService 是否已经在运行 ---
function Get-SignServiceProcess {
    return Get-CimInstance Win32_Process -Filter "Name LIKE 'dotnet%'" | 
           Where-Object { $_.CommandLine -like "*AllLive.SignService*" }
}

try {
    Write-Host "[1/3] 检查后台服务状态..." -ForegroundColor Cyan
    $existingService = Get-SignServiceProcess
    
    if ($existingService) {
        Write-Host "[验证] SignService (dotnet) 已在后台运行。" -ForegroundColor Gray
    } else {
        Write-Host "[启动] 正在执行 $serviceScriptName ..." -ForegroundColor Green
        $fullScriptPath = Join-Path $workDir $serviceScriptName
        if (Test-Path $fullScriptPath) {
            # 启动脚本，输出将直接显示在此窗口
            $serviceProc = Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$fullScriptPath`"" -NoNewWindow -PassThru
            Start-Sleep -Seconds 2 # 等待 dotnet 启动输出
        } else {
            Write-Host "[错误] 找不到脚本: $fullScriptPath" -ForegroundColor Red
            Pause
            exit
        }
    }

    Write-Host "[2/3] 检查主程序状态..." -ForegroundColor Cyan
    $uwpProc = Get-Process -Name $uwpProcessName -ErrorAction SilentlyContinue

    if ($uwpProc) {
        Write-Host "[系统] UWP 程序已在运行，正在切换窗口..." -ForegroundColor Yellow
        $handle = $uwpProc.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            [Win32Functions.Win32SetForegroundWindow]::ShowWindow($handle, 9)
            [Win32Functions.Win32SetForegroundWindow]::SetForegroundWindow($handle)
        }
    } else {
        Write-Host "[系统] 正在启动 UWP 主程序..." -ForegroundColor Green
        Start-Process "shell:AppsFolder\$uwpAppId"
        
        # 等待进程启动
        $retry = 0
        while (-not $uwpProc -and $retry -lt 40) {
            Start-Sleep -Milliseconds 500
            $uwpProc = Get-Process -Name $uwpProcessName -ErrorAction SilentlyContinue
            $retry++
        }
    }

    Write-Host "[3/3] 进入监控模式..." -ForegroundColor Cyan
    if ($uwpProc) {
        Write-Host "[提示] 当你关闭 UWP 程序时，脚本会自动清理并退出。" -ForegroundColor Gray
        Write-Host "---------------- 日志输出区 ----------------" -ForegroundColor White
        
        # 阻塞等待
        $uwpProc.WaitForExit()
    }

}
catch {
    Write-Host "[错误] 脚本运行异常: $_" -ForegroundColor Red
}
finally {
    Write-Host "`n[清理] 检测到程序关闭，正在清理后台服务..." -ForegroundColor Yellow
    
    # 清理所有相关的 dotnet 进程
    $servicesToKill = Get-SignServiceProcess
    if ($servicesToKill) {
        foreach ($p in $servicesToKill) {
            Write-Host "[清理] 停止进程: $($p.ProcessId)" -ForegroundColor Gray
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "[系统] 清理完毕。" -ForegroundColor White
    if ($launcherMutex) {
        try {
            $launcherMutex.ReleaseMutex()
        } catch {}
        $launcherMutex.Dispose()
    }
    Start-Sleep -Seconds 1
    [System.Environment]::Exit(0)
}
