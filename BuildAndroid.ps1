# 1. Читаем текущую версию из файла
 $versionFile = "version.txt"
if (Test-Path $versionFile) {
    $buildNumber = [int](Get-Content $versionFile)
} else {
    $buildNumber = 1
}

# Увеличиваем версию на 1 и сохраняем
 $buildNumber++
Set-Content $versionFile $buildNumber

# Формируем красивую версию (например, 1.0.36)
 $displayVersion = "1.0.$buildNumber"

Write-Host "Updating .csproj to version $displayVersion..." -ForegroundColor Yellow

# 2. Автоматически обновляем файл CashPlace.csproj
 $csprojPath = "CashPlace.csproj"
if (Test-Path $csprojPath) {
    $content = Get-Content $csprojPath -Raw
    
    # Заменяем строки с версиями в файле
    $content = $content -replace '<ApplicationDisplayVersion>.*?</ApplicationDisplayVersion>', "<ApplicationDisplayVersion>$displayVersion</ApplicationDisplayVersion>"
    $content = $content -replace '<ApplicationVersion>.*?</ApplicationVersion>', "<ApplicationVersion>$buildNumber</ApplicationVersion>"
    
    # Сохраняем изменения
    Set-Content $csprojPath $content
} else {
    Write-Host "Error: CashPlace.csproj not found!" -ForegroundColor Red
    return
}

Write-Host "Building version $displayVersion..." -ForegroundColor Green

# 3. Собираем APK
dotnet build -f net8.0-android -c Release -p:AndroidPackageFormat=apk -p:ApplicationVersion=$buildNumber -p:ApplicationDisplayVersion=$displayVersion

# 4. Ищем ЛЮБОЙ собранный APK (берем самый свежий по дате изменения)
 $apkPath = Get-ChildItem -Path ".\bin\Release\net8.0-android\" -Filter "*.apk" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($apkPath) {
    Write-Host "APK found: $($apkPath.Name)" -ForegroundColor Green
    
    # 5. Папка на сервере
    $serverDir = "C:\DistrCashProgram\Russia\"
    
    # Имена файлов на сервере (всегда одинаковые, чтобы веб-сервис их находил)
    $serverApkName = "com.CashPlaceRegistrator.cash8mobile-Signed.apk"
    $serverTxtName = "com.CashPlaceRegistrator.cash8mobile-Signed.txt"

    # Копируем APK на сервер (под правильным именем)
    Copy-Item $apkPath.FullName -Destination (Join-Path $serverDir $serverApkName) -Force
    
    # Создаем текстовый файл с версией на сервере
    Set-Content -Path (Join-Path $serverDir $serverTxtName) -Value $displayVersion

    Write-Host "Files copied to $serverDir" -ForegroundColor Cyan
    Write-Host "Version in txt file: $displayVersion" -ForegroundColor Cyan
} else {
    Write-Host "Error: APK file not found in bin\Release\net8.0-android\!" -ForegroundColor Red
}