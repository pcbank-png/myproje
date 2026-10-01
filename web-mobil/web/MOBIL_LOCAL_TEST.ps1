param(
    [int]$Port = 8765,
    [string]$DbPath = ""
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'wwwroot'))
Add-Type -AssemblyName System.Web

function Write-Info([string]$text, [ConsoleColor]$color = [ConsoleColor]::Gray) {
    Write-Host $text -ForegroundColor $color
}

function Find-CariDatabase([string]$requestedPath) {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($requestedPath)) {
        $candidates.Add([IO.Path]::GetFullPath($requestedPath))
    }

    if (-not [string]::IsNullOrWhiteSpace($env:NSX_CARI_DB_PATH)) {
        $candidates.Add([IO.Path]::GetFullPath($env:NSX_CARI_DB_PATH))
    }

    $storageConfig = Join-Path $env:LOCALAPPDATA 'NSX Yazılım\NSX Cari Takip Pro Bulut\storage-drive.txt'
    if (Test-Path -LiteralPath $storageConfig -PathType Leaf) {
        try {
            $configuredRoot = (Get-Content -LiteralPath $storageConfig -Raw).Trim()
            if (-not [string]::IsNullOrWhiteSpace($configuredRoot)) {
                $candidates.Add((Join-Path $configuredRoot 'NSX_BACKUP\NSX Cari Takip Pro Bulut\Data\nsx_veresiye_defteri.db'))
            }
        } catch { }
    }

    # Masaustu programinin varsayilan secimi: D varsa D, yoksa C.
    foreach ($preferredRoot in @('D:\','C:\')) {
        if (Test-Path -LiteralPath $preferredRoot) {
            $candidates.Add((Join-Path $preferredRoot 'NSX_BACKUP\NSX Cari Takip Pro Bulut\Data\nsx_veresiye_defteri.db'))
        }
    }

    foreach ($drive in Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue) {
        if ([string]::IsNullOrWhiteSpace($drive.Root) -or $drive.Root -in @('C:\','D:\')) { continue }
        $candidates.Add((Join-Path $drive.Root 'NSX_BACKUP\NSX Cari Takip Pro Bulut\Data\nsx_veresiye_defteri.db'))
    }

    $legacyRoots = @(
        "$env:APPDATA\NSX\VeresiyeDefteri\Data\nsx_veresiye_defteri.db"
    )
    foreach ($legacy in $legacyRoots) {
        if (-not [string]::IsNullOrWhiteSpace($legacy)) { $candidates.Add($legacy) }
    }

    $seen = @{}
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $full = [IO.Path]::GetFullPath($candidate)
        if ($seen.ContainsKey($full)) { continue }
        $seen[$full] = $true
        if (Test-Path -LiteralPath $full -PathType Leaf) { return $full }
    }
    return $null
}

if (-not ('NsxWinSqlite' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class NsxWinSqlite
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_INTEGER = 1;
    private const int SQLITE_FLOAT = 2;
    private const int SQLITE_TEXT = 3;
    private const int SQLITE_BLOB = 4;
    private const int SQLITE_NULL = 5;
    private const int SQLITE_OPEN_READONLY = 0x00000001;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(IntPtr filename, out IntPtr db, int flags, IntPtr zvfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, IntPtr sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_name(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    private static IntPtr AllocUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes((value ?? String.Empty) + "\0");
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static string ReadUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return String.Empty;
        int len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;
        if (len == 0) return String.Empty;
        byte[] bytes = new byte[len];
        Marshal.Copy(ptr, bytes, 0, len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static Exception DbError(IntPtr db, string prefix)
    {
        string detail = db == IntPtr.Zero ? "SQLite açılamadı." : ReadUtf8(sqlite3_errmsg(db));
        return new InvalidOperationException(prefix + " " + detail);
    }

    public static List<Dictionary<string, object>> Query(string dbPath, string sql)
    {
        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;
        IntPtr pathPtr = IntPtr.Zero;
        IntPtr sqlPtr = IntPtr.Zero;
        try
        {
            pathPtr = AllocUtf8(dbPath);
            int rc = sqlite3_open_v2(pathPtr, out db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != SQLITE_OK) throw DbError(db, "Cari veritabanı açılamadı.");
            sqlite3_busy_timeout(db, 5000);

            sqlPtr = AllocUtf8(sql);
            rc = sqlite3_prepare_v2(db, sqlPtr, -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) throw DbError(db, "SQLite sorgusu hazırlanamadı.");

            var rows = new List<Dictionary<string, object>>();
            int columnCount = sqlite3_column_count(stmt);
            while (true)
            {
                rc = sqlite3_step(stmt);
                if (rc == SQLITE_DONE) break;
                if (rc != SQLITE_ROW) throw DbError(db, "SQLite sorgusu çalıştırılamadı.");

                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < columnCount; i++)
                {
                    string name = ReadUtf8(sqlite3_column_name(stmt, i));
                    int type = sqlite3_column_type(stmt, i);
                    object value;
                    switch (type)
                    {
                        case SQLITE_INTEGER: value = sqlite3_column_int64(stmt, i); break;
                        case SQLITE_FLOAT: value = sqlite3_column_double(stmt, i); break;
                        case SQLITE_TEXT: value = ReadUtf8(sqlite3_column_text(stmt, i)); break;
                        case SQLITE_NULL: value = null; break;
                        case SQLITE_BLOB: value = null; break;
                        default: value = ReadUtf8(sqlite3_column_text(stmt, i)); break;
                    }
                    row[name] = value;
                }
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            if (sqlPtr != IntPtr.Zero) Marshal.FreeHGlobal(sqlPtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
        }
    }
}
'@
}

$resolvedDb = Find-CariDatabase $DbPath
if ([string]::IsNullOrWhiteSpace($resolvedDb)) {
    Write-Info ''
    Write-Info 'NSX Cari Takip Pro Bulut - GERCEK LOCAL MOBIL TEST' Cyan
    Write-Info 'Cari veritabani otomatik bulunamadi.' Red
    Write-Info 'Beklenen konumlardan biri:' Yellow
    Write-Info '  D:\NSX_BACKUP\NSX Cari Takip Pro Bulut\Data\nsx_veresiye_defteri.db'
    Write-Info '  C:\NSX_BACKUP\NSX Cari Takip Pro Bulut\Data\nsx_veresiye_defteri.db'
    Write-Info ''
    Write-Info 'Ozel yol kullanmak icin:' Yellow
    Write-Info '  powershell -ExecutionPolicy Bypass -File .\MOBIL_LOCAL_TEST.ps1 -DbPath "D:\...\nsx_veresiye_defteri.db"'
    Read-Host 'Kapatmak icin Enter'
    exit 1
}
$DbPath = [IO.Path]::GetFullPath($resolvedDb)

function Invoke-Sql([string]$sql) {
    return @([NsxWinSqlite]::Query($DbPath, $sql))
}

function Get-RowValue($row, [string]$name, $default = $null) {
    if ($null -eq $row) { return $default }
    if ($row.ContainsKey($name) -and $null -ne $row[$name]) { return $row[$name] }
    return $default
}

function To-Decimal($value) {
    if ($null -eq $value) { return 0.0 }
    if ($value -is [double] -or $value -is [float] -or $value -is [decimal] -or $value -is [int] -or $value -is [long]) {
        return [double]$value
    }
    $number = 0.0
    if ([double]::TryParse([string]$value, [Globalization.NumberStyles]::Any, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { return $number }
    if ([double]::TryParse([string]$value, [ref]$number)) { return $number }
    return 0.0
}

function To-Iso($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return [DateTime]::UtcNow.ToString('o') }
    $dt = [DateTime]::MinValue
    if ([DateTime]::TryParse([string]$value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeLocal, [ref]$dt) -or
        [DateTime]::TryParse([string]$value, [ref]$dt)) {
        return $dt.ToUniversalTime().ToString('o')
    }
    return [string]$value
}

function Get-DataCursor {
    $latest = [DateTime]::MinValue
    foreach ($path in @($DbPath, "$DbPath-wal", "$DbPath-shm")) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $stamp = (Get-Item -LiteralPath $path).LastWriteTimeUtc
            if ($stamp -gt $latest) { $latest = $stamp }
        }
    }
    if ($latest -eq [DateTime]::MinValue) { $latest = [DateTime]::UtcNow }
    $epoch = [DateTime]::SpecifyKind([DateTime]'1970-01-01 00:00:00', [DateTimeKind]::Utc)
    return [long][Math]::Floor(($latest - $epoch).TotalMilliseconds)
}

function Get-SelectedCompanyId {
    try {
        $rows = @(Invoke-Sql "SELECT Value FROM AppSettings WHERE Key='SelectedCompanyId' LIMIT 1;")
        if ($rows.Count -gt 0) {
            $id = 0
            if ([int]::TryParse([string](Get-RowValue $rows[0] 'Value' ''), [ref]$id) -and $id -gt 0) { return $id }
        }
    } catch { }
    $rows = @(Invoke-Sql 'SELECT Id FROM Companies ORDER BY Id LIMIT 1;')
    if ($rows.Count -eq 0) { return 0 }
    return [int](Get-RowValue $rows[0] 'Id' 0)
}

function Get-Companies {
    $rows = @(Invoke-Sql @'
SELECT Id,Name,AuthorizedPerson,Address,City,District,Phone,Email,TaxOffice,TaxNumber,CreatedAt
FROM Companies ORDER BY Name,Id;
'@)
    $result = @()
    foreach ($row in $rows) {
        $id = [int](Get-RowValue $row 'Id' 0)
        $result += [ordered]@{
            entityType = 'company'
            entityId = "company-$id"
            payload = [ordered]@{
                name = [string](Get-RowValue $row 'Name' '')
                authorizedPerson = [string](Get-RowValue $row 'AuthorizedPerson' '')
                address = [string](Get-RowValue $row 'Address' '')
                city = [string](Get-RowValue $row 'City' '')
                district = [string](Get-RowValue $row 'District' '')
                phone = [string](Get-RowValue $row 'Phone' '')
                email = [string](Get-RowValue $row 'Email' '')
                taxOffice = [string](Get-RowValue $row 'TaxOffice' '')
                taxNumber = [string](Get-RowValue $row 'TaxNumber' '')
            }
            version = 1
            cursor = (Get-DataCursor)
            updatedAtUtc = To-Iso (Get-RowValue $row 'CreatedAt' $null)
        }
    }
    return @($result)
}

function Get-CustomerTotals {
    $debt = @{}
    $collection = @{}
    foreach ($row in (Invoke-Sql 'SELECT CustomerId,COALESCE(SUM(CAST(TotalPrice AS REAL)),0) AS Total FROM ServiceRecords GROUP BY CustomerId;')) {
        $debt[[int](Get-RowValue $row 'CustomerId' 0)] = To-Decimal (Get-RowValue $row 'Total' 0)
    }
    foreach ($row in (Invoke-Sql @'
SELECT s.CustomerId,COALESCE(SUM(CAST(p.Amount AS REAL)),0) AS Total
FROM Payments p INNER JOIN ServiceRecords s ON s.Id=p.ServiceRecordId
GROUP BY s.CustomerId;
'@)) {
        $collection[[int](Get-RowValue $row 'CustomerId' 0)] = To-Decimal (Get-RowValue $row 'Total' 0)
    }
    return @{ Debt = $debt; Collection = $collection }
}

function Get-Customers([string]$search = '', [int]$take = 500) {
    $rows = @(Invoke-Sql @'
SELECT Id,FullName,Phone,BarcodeNo,Email,Address,TaxOffice,TaxNumber,City,District,RiskLimit,IsActive,CompanyId,CreatedAt
FROM Customers ORDER BY FullName,Id;
'@)
    $totals = Get-CustomerTotals
    if ($null -eq $search) { $search = '' }
    $needle = $search.Trim().ToLowerInvariant()
    $take = [Math]::Max(1, [Math]::Min(500, $take))
    # Windows PowerShell 5.1 generic List[object] + @() kombinasyonu bazı sistemlerde
    # 'Argument types do not match' hatası verebiliyor. Native PowerShell dizisi kullan.
    $result = @()
    foreach ($row in $rows) {
        $id = [int](Get-RowValue $row 'Id' 0)
        $name = [string](Get-RowValue $row 'FullName' '')
        $phone = [string](Get-RowValue $row 'Phone' '')
        $barcode = [string](Get-RowValue $row 'BarcodeNo' '')
        $email = [string](Get-RowValue $row 'Email' '')
        if ($needle.Length -gt 0) {
            $hay = ($name + ' ' + $phone + ' ' + $barcode + ' ' + $email).ToLowerInvariant()
            if (-not $hay.Contains($needle)) { continue }
        }
        $companyId = [int](Get-RowValue $row 'CompanyId' 0)
        $debtTotal = if ($totals.Debt.ContainsKey($id)) { [double]$totals.Debt[$id] } else { 0.0 }
        $collectionTotal = if ($totals.Collection.ContainsKey($id)) { [double]$totals.Collection[$id] } else { 0.0 }
        $result += [pscustomobject][ordered]@{
            entityId = "customer-$id"
            payload = [ordered]@{
                companyId = if ($companyId -gt 0) { "company-$companyId" } else { $null }
                name = $name
                barcodeNo = $barcode
                phone = $phone
                email = $email
                address = [string](Get-RowValue $row 'Address' '')
                taxOffice = [string](Get-RowValue $row 'TaxOffice' '')
                taxNumber = [string](Get-RowValue $row 'TaxNumber' '')
                city = [string](Get-RowValue $row 'City' '')
                district = [string](Get-RowValue $row 'District' '')
                riskLimit = To-Decimal (Get-RowValue $row 'RiskLimit' 0)
                isActive = ([int](Get-RowValue $row 'IsActive' 1) -ne 0)
            }
            version = 1
            cursor = (Get-DataCursor)
            updatedAtUtc = To-Iso (Get-RowValue $row 'CreatedAt' $null)
            debtTotal = $debtTotal
            collectionTotal = $collectionTotal
            balance = $debtTotal - $collectionTotal
        }
        if ($result.Count -ge $take) { break }
    }
    return $result
}

function Convert-TransactionRow($row) {
    $type = [string](Get-RowValue $row 'EntityType' '')
    $id = [int](Get-RowValue $row 'Id' 0)
    $customerId = [int](Get-RowValue $row 'CustomerId' 0)
    return [ordered]@{
        entityType = $type
        entityId = "$type-$id"
        payload = [ordered]@{
            customerId = "customer-$customerId"
            amount = To-Decimal (Get-RowValue $row 'Amount' 0)
            transactionDateUtc = To-Iso (Get-RowValue $row 'TransactionDateUtc' $null)
            description = [string](Get-RowValue $row 'Description' '')
            transactionKind = [string](Get-RowValue $row 'TransactionKind' '')
        }
        isDeleted = $false
        version = 1
        cursor = (Get-DataCursor)
        createdAtUtc = To-Iso (Get-RowValue $row 'TransactionDateUtc' $null)
        updatedAtUtc = To-Iso (Get-RowValue $row 'TransactionDateUtc' $null)
    }
}

function Get-Transactions([int]$customerId = 0, [int]$take = 100) {
    $customerFilterDebt = if ($customerId -gt 0) { "WHERE sr.CustomerId=$customerId" } else { '' }
    $customerFilterPayment = if ($customerId -gt 0) { "WHERE sr.CustomerId=$customerId" } else { '' }
    $sql = @"
SELECT 'debt' AS EntityType,sr.Id AS Id,sr.CustomerId AS CustomerId,sr.TotalPrice AS Amount,
       sr.CreatedAt AS TransactionDateUtc,sr.FaultDescription AS Description,sr.TransactionKind AS TransactionKind
FROM ServiceRecords sr $customerFilterDebt
UNION ALL
SELECT 'collection' AS EntityType,p.Id AS Id,sr.CustomerId AS CustomerId,p.Amount AS Amount,
       p.PaymentDate AS TransactionDateUtc,COALESCE(NULLIF(p.Note,''),p.PaymentType,'') AS Description,p.TransactionKind AS TransactionKind
FROM Payments p INNER JOIN ServiceRecords sr ON sr.Id=p.ServiceRecordId $customerFilterPayment;
"@
    $rows = @(Invoke-Sql $sql)
    $items = foreach ($row in $rows) { Convert-TransactionRow $row }
    $sorted = @($items | Sort-Object { try { [DateTime]$_.payload.transactionDateUtc } catch { [DateTime]::MinValue } } -Descending)
    $take = [Math]::Max(1, [Math]::Min(1000, $take))
    return @($sorted | Select-Object -First $take)
}

function Get-CompanyName {
    $companies = @(Get-Companies)
    if ($companies.Count -eq 0) { return 'NSX Cari Takip Pro Bulut' }
    $selected = Get-SelectedCompanyId
    $match = $companies | Where-Object { $_.entityId -eq "company-$selected" } | Select-Object -First 1
    if ($null -eq $match) { $match = $companies[0] }
    $name = [string]$match.payload.name
    return $(if ([string]::IsNullOrWhiteSpace($name)) { 'NSX Cari Takip Pro Bulut' } else { $name })
}

function Get-Bootstrap {
    $cursor = Get-DataCursor
    return [ordered]@{
        companyName = Get-CompanyName
        status = [ordered]@{ cursor = $cursor; source = 'local-desktop-sqlite'; readOnly = $true }
        customers = @(Get-Customers '' 500)
        companies = @(Get-Companies)
        recentTransactions = @(Get-Transactions 0 100)
    }
}

function Send-Response($stream, [int]$statusCode, [string]$statusText, [string]$contentType, [byte[]]$bytes, [hashtable]$extraHeaders = $null) {
    $headers = "HTTP/1.1 $statusCode $statusText`r`nContent-Type: $contentType`r`nContent-Length: $($bytes.Length)`r`nCache-Control: no-store`r`nConnection: close`r`n"
    if ($null -ne $extraHeaders) {
        foreach ($key in $extraHeaders.Keys) { $headers += "$key`: $($extraHeaders[$key])`r`n" }
    }
    $headers += "`r`n"
    $headerBytes = [Text.Encoding]::ASCII.GetBytes($headers)
    $stream.Write($headerBytes, 0, $headerBytes.Length)
    if ($bytes.Length -gt 0) { $stream.Write($bytes, 0, $bytes.Length) }
    $stream.Flush()
}

function Send-Json($stream, [int]$statusCode, $value) {
    $statusText = if ($statusCode -eq 200) { 'OK' } elseif ($statusCode -eq 404) { 'Not Found' } elseif ($statusCode -eq 409) { 'Conflict' } else { 'Error' }
    $json = $value | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    Send-Response $stream $statusCode $statusText 'application/json; charset=utf-8' $bytes
}

function Handle-Api($stream, [string]$method, [Uri]$uri) {
    $path = $uri.AbsolutePath.ToLowerInvariant()
    $prefix = '/api/caritakipcloud'
    $relative = $path.Substring($prefix.Length)

    if ($method -ne 'GET') {
        if ($relative -eq '/mobile/session/revoke' -and $method -eq 'POST') {
            Send-Json $stream 200 ([ordered]@{ success = $true; localReadOnly = $true })
            return
        }
        Send-Json $stream 409 ([ordered]@{
            message = 'Gerçek local mobil test güvenliği: masaüstü veritabanına yazma kapalıdır. Bu aşamada yalnızca gerçek veriler okunur.'
            code = 'local_read_only'
        })
        return
    }

    if ($relative -eq '/mobile/session/bootstrap') {
        Send-Json $stream 200 (Get-Bootstrap)
        return
    }
    if ($relative -eq '/mobile/status') {
        Send-Json $stream 200 ([ordered]@{ cursor = Get-DataCursor; source = 'local-desktop-sqlite'; readOnly = $true })
        return
    }
    if ($relative -eq '/mobile/companies') {
        Send-Json $stream 200 @(Get-Companies)
        return
    }
    if ($relative -eq '/mobile/customers') {
        $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
        $take = 500
        [int]::TryParse($query['take'], [ref]$take) | Out-Null
        Send-Json $stream 200 @(Get-Customers ([string]$query['search']) $take)
        return
    }
    if ($relative -eq '/mobile/transactions/recent') {
        $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
        $take = 100
        [int]::TryParse($query['take'], [ref]$take) | Out-Null
        Send-Json $stream 200 @(Get-Transactions 0 $take)
        return
    }
    if ($relative -match '^/mobile/customers/customer-(\d+)/transactions$') {
        Send-Json $stream 200 @(Get-Transactions ([int]$Matches[1]) 1000)
        return
    }
    if ($relative -eq '/health') {
        Send-Json $stream 200 ([ordered]@{ name='NSX Cari Takip Pro Bulut - Local Real Data'; status='healthy'; database=$DbPath; cursor=Get-DataCursor })
        return
    }

    Send-Json $stream 404 ([ordered]@{ message = 'Local API endpoint bulunamadı.' })
}

$requiredTables = @('Companies','Customers','ServiceRecords','Payments','AppSettings')
$tableRows = @(Invoke-Sql "SELECT name FROM sqlite_master WHERE type='table';")
$tableNames = @($tableRows | ForEach-Object { [string](Get-RowValue $_ 'name' '') })
$missing = @($requiredTables | Where-Object { $tableNames -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Info "Veritabani Cari Takip semasi degil. Eksik tablolar: $($missing -join ', ')" Red
    Read-Host 'Kapatmak icin Enter'
    exit 1
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try {
    $listener.Start()
} catch {
    Write-Info "Port $Port kullanilamiyor. Farkli port deneyin: .\MOBIL_LOCAL_TEST.ps1 -Port 8766" Red
    Read-Host 'Kapatmak icin Enter'
    exit 1
}

$baseUrl = "http://127.0.0.1:$Port/"
$customerCount = (@(Invoke-Sql 'SELECT COUNT(*) AS Count FROM Customers;'))[0]['Count']
$debtCount = (@(Invoke-Sql 'SELECT COUNT(*) AS Count FROM ServiceRecords;'))[0]['Count']
$paymentCount = (@(Invoke-Sql 'SELECT COUNT(*) AS Count FROM Payments;'))[0]['Count']

Write-Info ''
Write-Info '============================================================' DarkCyan
Write-Info ' NSX Cari Takip Pro Bulut - GERCEK LOCAL MOBIL TEST' Cyan
Write-Info '============================================================' DarkCyan
Write-Info "Veritabani : $DbPath" Gray
Write-Info "Firma       : $(Get-CompanyName)" Gray
Write-Info "Musteri     : $customerCount" Gray
Write-Info "Borc kaydi  : $debtCount" Gray
Write-Info "Tahsilat    : $paymentCount" Gray
Write-Info "Adres       : ${baseUrl}caritakip/index.html" Green
Write-Info ''
Write-Info 'GUVENLIK: Bu local test masaustu veritabanini SADECE OKUR.' Yellow
Write-Info 'Mobilde kaydet/sil islemleri bu asamada gercek DB ye yazilmaz.' Yellow
Write-Info 'Bu pencere acik kaldigi surece local mobil test calisir. Kapatmak icin Ctrl+C.' DarkGray

Start-Process "${baseUrl}caritakip/index.html"

$mime = @{
    '.html'='text/html; charset=utf-8'; '.css'='text/css; charset=utf-8'; '.js'='application/javascript; charset=utf-8';
    '.svg'='image/svg+xml'; '.json'='application/json; charset=utf-8'; '.webmanifest'='application/manifest+json; charset=utf-8';
    '.png'='image/png'; '.ico'='image/x-icon'; '.woff2'='font/woff2'; '.woff'='font/woff'
}

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::ASCII, $false, 8192, $true)
            $requestLine = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($requestLine)) { continue }

            $headers = @{}
            while (($line = $reader.ReadLine()) -ne $null -and $line -ne '') {
                $colon = $line.IndexOf(':')
                if ($colon -gt 0) { $headers[$line.Substring(0,$colon).Trim()] = $line.Substring($colon+1).Trim() }
            }

            if ($requestLine -notmatch '^([A-Z]+)\s+([^\s]+)') { continue }
            $method = $Matches[1].ToUpperInvariant()
            $target = $Matches[2]
            $uri = [Uri]("http://127.0.0.1" + $target)

            if ($uri.AbsolutePath.StartsWith('/Api/CaritakipCloud', [StringComparison]::OrdinalIgnoreCase)) {
                Handle-Api $stream $method $uri
                continue
            }

            if ($method -ne 'GET' -and $method -ne 'HEAD') {
                $bytes = [Text.Encoding]::UTF8.GetBytes('405 - Method Not Allowed')
                Send-Response $stream 405 'Method Not Allowed' 'text/plain; charset=utf-8' $bytes
                continue
            }

            $relative = [Uri]::UnescapeDataString($uri.AbsolutePath.TrimStart('/'))
            if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Equals('CaritakipCloud', [StringComparison]::OrdinalIgnoreCase)) {
                $relative = 'caritakip/index.html'
            }
            $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
            $ok = $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $full -PathType Leaf)
            if ($ok) {
                $bytes = [IO.File]::ReadAllBytes($full)
                $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
                $contentType = if ($mime.ContainsKey($ext)) { $mime[$ext] } else { 'application/octet-stream' }
                if ($method -eq 'HEAD') { $bytes = [byte[]]@() }
                Send-Response $stream 200 'OK' $contentType $bytes
            } else {
                $bytes = [Text.Encoding]::UTF8.GetBytes('404 - Dosya bulunamadi')
                Send-Response $stream 404 'Not Found' 'text/plain; charset=utf-8' $bytes
            }
        } catch {
            try {
                $message = $_.Exception.Message
                Write-Host ('LOCAL API HATA: ' + $message) -ForegroundColor Red
                if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) { Write-Host ('Satir: ' + $_.InvocationInfo.ScriptLineNumber) -ForegroundColor DarkGray }
                $bytes = [Text.Encoding]::UTF8.GetBytes((@{ message=$message } | ConvertTo-Json -Compress))
                Send-Response $stream 500 'Internal Server Error' 'application/json; charset=utf-8' $bytes
            } catch { }
        } finally {
            $client.Close()
        }
    }
} finally {
    $listener.Stop()
}
