# EfCore.FaultIsolation NuGet包发布脚本

# 设置包版本
$version = "1.0.0.4"
$packageId = "EfCore.FaultIsolation"

# 设置NuGet源和API密钥变量
# 注意：在实际使用前，请将YOUR_NUGET_API_KEY替换为您的实际API密钥
$nuGetSource = "https://api.nuget.org/v3/index.json"
$apiKey = "YOUR_NUGET_API_KEY"

# 清理和构建项目
Write-Host "清理项目..." -ForegroundColor Cyan
Remove-Item -Path ./bin -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ./obj -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ./nupkg -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "构建项目(Release模式)..." -ForegroundColor Cyan
dotnet build ./EfCore.FaultIsolation -c Release

# 打包项目
Write-Host "打包项目..." -ForegroundColor Cyan
New-Item -Path ./nupkg -ItemType Directory -Force
dotnet pack ./EfCore.FaultIsolation -c Release -o ./nupkg

# 发布包
Write-Host "发布NuGet包..." -ForegroundColor Cyan
$packagePath = "./nupkg/${packageId}.${version}.nupkg"

if (Test-Path $packagePath) {
    Write-Host "找到包文件: $packagePath" -ForegroundColor Green
    
    if ($apiKey -eq "YOUR_NUGET_API_KEY") {
        Write-Host "警告：请替换脚本中的YOUR_NUGET_API_KEY为您的实际NuGet API密钥" -ForegroundColor Yellow
        Write-Host "或者使用以下命令手动发布：" -ForegroundColor Yellow
        Write-Host "dotnet nuget push $packagePath -s $nuGetSource -k YOUR_NUGET_API_KEY" -ForegroundColor Green
    } else {
        dotnet nuget push $packagePath -s $nuGetSource -k $apiKey
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "包发布成功！" -ForegroundColor Green
            Write-Host "包信息："
            Write-Host "  ID: $packageId" -ForegroundColor Cyan
            Write-Host "  版本: $version" -ForegroundColor Cyan
            Write-Host "  源: $nuGetSource" -ForegroundColor Cyan
        } else {
            Write-Host "包发布失败！" -ForegroundColor Red
            exit 1
        }
    }
} else {
    Write-Host "未找到包文件: $packagePath" -ForegroundColor Red
    exit 1
}

Write-Host "发布脚本执行完成！" -ForegroundColor Green
