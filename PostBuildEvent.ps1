param(
    $assembly = "akka.websocket"
    , $build = "Release"
)


function Get-VersionFromFileName {
    param (
        [string]$fileName
    )

    # 提取類似於 "1.0"、"10.2.3"、"3.4.5.6" 等的版本號
    if ($fileName -match '\d+(\.\d+)+') {
        return [version]$matches[0] # 轉換為 [version] 類型進行數值比較
    } else {
        return [version]'0.0' # 如果找不到版本號，則返回最低版本
    }
}

function Get-LibPacksContent {
    $currentDir = Get-Location

    while ($currentDir -ne [System.IO.Path]::GetPathRoot($currentDir)) {
        $libPacksPath = Join-Path -Path $currentDir -ChildPath 'lib-packs.txt'
        if (Test-Path $libPacksPath) {
            return Get-Content -Path $libPacksPath -Raw
        }
        $currentDir = (Get-Item $currentDir).Parent.FullName
    }

    Write-Host "lib-packs.txt not found in any parent directory." -ForegroundColor Red
    return $null
}

write-host ("[PostBuilkd]" + $assembly + ': Current path: ' + (pwd).path)
cd ./bin/$build/net472
try {
    #$pkg = (dir "$($assembly)*.nupkg" | Sort-Object -Property { Get-VersionFromFileName $_.Name } -Descending)[0]
    #write-host "<dotnet nuget push $($pkg.Name)>"
    #invoke-expression "dotnet nuget push $($pkg.Name) --api-key $(gc G:\Nuget\apikey.txt) --source https://api.nuget.org/v3/index.json  --skip-duplicate"
    #copy   $pkg.FullName "C:\Program Files\dotnet\sdk\7.0.410\FSharp\library-packs" -force
    #copy   $pkg.FullName "C:\Program Files\dotnet\sdk\8.0.100-rc.2.23502.2\FSharp\library-packs" -force
    #copy   $pkg.FullName "C:\Program Files\dotnet\sdk\8.0.302\FSharp\library-packs" -force
    #copy   $pkg.FullName $(Get-LibPacksContent) -force
    copy   "$($assembly)" "../../../../DB2Query/DB2FQueryWrapper/Assemblies" -force
    copy   "$($assembly)" "//10.36.206.244/s$/PFCF_RT" -force
}
catch {
    write-host "=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+="
    write-host $_
    write-host "=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+="
}

cd ../
